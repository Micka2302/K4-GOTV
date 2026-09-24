using System.Text;
using System.Text.Json;
using FluentFTP;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Core.Translations;
using Microsoft.Extensions.Logging;

namespace K4GOTV;

[MinimumApiVersion(375)]
public sealed partial class Plugin : BasePlugin, IPluginConfig<PluginConfig>
{
	public override string ModuleName => "K4-GOTV";
	public override string ModuleDescription => "Advanced GOTV handler with Discord and FTP integration";
	public override string ModuleVersion => "2.1.6";
	public override string ModuleAuthor => "K4ryuu @ KitsuneLab";

	public required PluginConfig Config { get; set; } = new PluginConfig();

	private string? fileName = null;
	private double LastPlayerCheckTime;
	private double DemoStartTime = 0.0;
	private int maxFileSizeInMB = 25;
	private string demoDirectoryPath = string.Empty;
	private bool mapChangePending = false;
	private string? forwardedRecordPath;
	private int recordingGeneration;
	private string[] recordingPaths = [];
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> pendingDemoNames = new();
	private string CsgoDirectory => DemoPaths.GetCsgoDirectory(Server.GameDirectory);
	private string DemoDirectory => demoDirectoryPath;
	private UploadService? uploadService;
	private string RetentionFilePath => Path.Combine(ModuleDirectory, "uploads_retention.json");
	private record UploadRetentionRecord(string Identifier, DateTime UploadedAt);

	public override void Load(bool hotReload)
	{
		AddCommandListener("tv_record", CommandListener_Record, HookMode.Pre);
		AddCommandListener("tv_stoprecord", CommandListener_StopRecord, HookMode.Post);
		AddCommandListener("changelevel", CommandListener_Changelevel, HookMode.Pre);
		AddCommandListener("map", CommandListener_Changelevel, HookMode.Pre);
		AddCommandListener("host_workshop_map", CommandListener_Changelevel, HookMode.Pre);
		AddCommandListener("ds_workshop_changelevel", CommandListener_Changelevel, HookMode.Pre);

		RegisterEventHandler((EventCsWinPanelMatch @event, GameEventInfo info) =>
		{
			mapChangePending = true;
			Server.ExecuteCommand("tv_stoprecord");
			return HookResult.Continue;
		});

		RegisterListener<Listeners.OnMapEnd>(() =>
		{
			mapChangePending = true;
			if (!string.IsNullOrEmpty(fileName))
				Server.ExecuteCommand("tv_stoprecord");
		});

		RegisterListener<Listeners.OnMapStart>((mapName) =>
		{
			ResetVariables();
			mapChangePending = false;
		});

		RegisterEventHandler((EventRoundStart @event, GameEventInfo info) =>
		{
			if (Config.AutoRecord.Enabled)
			{
				if (Config.AutoRecord.CropRounds && !string.IsNullOrEmpty(fileName))
					Server.ExecuteCommand("tv_stoprecord");

				Server.NextWorldUpdate(TryAutoRecord);
			}
			return HookResult.Continue;
		});

		RegisterEventHandler((EventPlayerActivate @event, GameEventInfo info) =>
		{
			var player = @event.Userid;
			if (player?.IsValid == true && !player.IsBot && !player.IsHLTV)
				LastPlayerCheckTime = Server.EngineTime;

			Server.NextWorldUpdate(TryAutoRecord);

			return HookResult.Continue;
		});

		EnsureDemoDirectory(Config);
		Logger.LogInformation("K4-GOTV {Version} loaded: recording directly in {DemoDirectory} using configured filenames and absolute paths.", ModuleVersion, DemoDirectory);
		// Retry when CSTV was not ready at player activation or round start.
		AddTimer(5.0f, TryAutoRecord, TimerFlags.REPEAT);

		if (Config.AutoRecord.StopOnIdle)
		{
			AddTimer(1.0f, () =>
			{
				if (DemoStartTime == 0.0)
					return;

				int _playerCount = PlayerCount();
				if (_playerCount < Config.AutoRecord.IdlePlayerCountThreshold)
				{
					double idleTime = Server.EngineTime - LastPlayerCheckTime;
					if (idleTime > Config.AutoRecord.IdleTimeSeconds)
					{
						Server.ExecuteCommand("tv_stoprecord");
						Logger.LogInformation($"Recording stopped due to inactivity exceeding {Config.AutoRecord.IdleTimeSeconds} seconds, player count: {_playerCount}.");
					}
				}
				else
					LastPlayerCheckTime = Server.EngineTime;
			}, TimerFlags.REPEAT);
		}

		if (hotReload)
			Server.NextWorldUpdate(TryAutoRecord);

		maxFileSizeInMB = (Config.Discord.ServerBoost == 2) ? 50 : (Config.Discord.ServerBoost == 3) ? 100 : 25;
		uploadService = new UploadService(Config, Logger);

		if (Config.General.AutoCleanupEnabled)
		{
			AddTimer(Config.General.AutoCleanupIntervalMinutes * 60f, () =>
			{
				var cutoff = DateTime.Now.AddHours(-Config.General.AutoCleanupFileAgeHours);
				var files = Directory.GetFiles(DemoDirectory, "*.dem").Concat(Directory.GetFiles(DemoDirectory, "*.zip"));
				foreach (var file in files)
				{
					if (File.GetCreationTime(file) < cutoff)
						_ = Task.Run(async () =>
						{
							if (!IsDemoInUse(file))
								await FileManager.DeleteFileAsync(file, Logger, Config.General.LogDeletions);
						});
				}
			}, TimerFlags.REPEAT);
		}

		if (Config.Ftp.RetentionEnabled)
		{
			AddTimer(3600f, () => _ = Task.Run(CleanFtpRetention), TimerFlags.REPEAT);
		}


	}

	public override void Unload(bool hotReload)
	{
		mapChangePending = true;
		Server.ExecuteCommand("tv_stoprecord");
		ResetVariables();
	}

	public override void OnAllPluginsLoaded(bool isReload)
	{
		_ = Task.Run(async () =>
		{
			if (Config.General.DeleteEveryDemoFromServerAfterServerStart)
			{
				var allFiles = Directory.GetFiles(DemoDirectory, "*.dem").Concat(Directory.GetFiles(DemoDirectory, "*.zip")).ToArray();

				foreach (var file in allFiles)
					if (!IsDemoInUse(file))
						await FileManager.DeleteFileAsync(file, Logger, Config.General.LogDeletions);
			}
		});
	}

	private HookResult CommandListener_Changelevel(CCSPlayerController? player, CommandInfo info)
	{
		mapChangePending = true;
		if (!string.IsNullOrEmpty(fileName))
			Server.ExecuteCommand("tv_stoprecord");

		return HookResult.Continue;
	}

	private HookResult CommandListener_Record(CCSPlayerController? player, CommandInfo info)
	{
		if (!Config.AutoRecord.Enabled)
			return HookResult.Continue;

		string fileNameArg = info.ArgCount > 1 ? info.GetArg(1) : string.Empty;
		// Only our rewritten command may pass through while a demo is reserved.
		if (forwardedRecordPath != null && fileNameArg == forwardedRecordPath)
		{
			forwardedRecordPath = null;
			return HookResult.Continue;
		}
		if (mapChangePending || fileName != null)
			return HookResult.Stop;

		if (!Config.AutoRecord.RecordWarmup && GameRules()?.GameRules?.WarmupPeriod == true)
			return HookResult.Stop;

		DemoStartTime = Server.EngineTime;
		LastPlayerCheckTime = DemoStartTime;
		string baseName = string.IsNullOrEmpty(fileNameArg) ? Config.General.DefaultFileName : fileNameArg;
		string pattern = (Config.AutoRecord.Enabled && Config.AutoRecord.CropRounds)
			? Config.General.CropRoundsFileNamingPattern
			: Config.General.RegularFileNamingPattern;
		fileName = ReplacePlaceholdersForFileName(pattern, baseName);
		// Map names (including workshop paths) must remain a single filename.
		fileName = string.Concat(fileName.Select(c => char.IsControl(c) || "<>:\"/\\|?*;".Contains(c) ? '_' : c));
		string uniqueBaseName = fileName;

		string fullPath = Path.Combine(DemoDirectory, $"{fileName}.dem");
		int counter = 1;
		while (File.Exists(fullPath) || File.Exists(Path.ChangeExtension(fullPath, ".zip")) || pendingDemoNames.ContainsKey(fileName))
		{
			fileName = $"{uniqueBaseName}_{counter}";
			fullPath = Path.Combine(DemoDirectory, $"{fileName}.dem");
			counter++;
		}

		// A relative path may resolve under addons/metamod instead of csgo.
		// Use the final absolute path and configured filename from the start.
		string commandPath = DemoPaths.GetRecordingPath(DemoDirectory, $"{fileName}.dem");
		recordingPaths = [commandPath];
		string[] candidatePaths = recordingPaths;
		forwardedRecordPath = commandPath;
		int generation = ++recordingGeneration;
		Server.NextWorldUpdate(() =>
		{
			if (generation != recordingGeneration || mapChangePending)
				return;
			Logger.LogInformation("Requesting demo recording: {DemoPath}", commandPath);
			Server.ExecuteCommand($"tv_record \"{commandPath}\"");
		});
		// A queued command is not proof that the engine actually opened a demo.
		float timeout = Math.Max(30f, (ConVar.Find("tv_delay")?.GetPrimitiveValue<int>() ?? 0) + 15f);
		CounterStrikeSharp.API.Modules.Timers.Timer? confirmationTimer = null;
		confirmationTimer = AddTimer(1f, () =>
		{
			if (generation != recordingGeneration)
			{
				confirmationTimer?.Kill();
				return;
			}
			string? recordedPath = candidatePaths.FirstOrDefault(path => File.Exists(path) && new FileInfo(path).Length > 0);
			if (recordedPath != null)
			{
				Logger.LogInformation("Demo recording confirmed: {DemoPath}", recordedPath);
				AnnounceRecordingStart();
				confirmationTimer?.Kill();
			}
			else if (Server.EngineTime - DemoStartTime >= timeout)
			{
				Logger.LogError("CSTV did not create a non-empty demo. Checked: {DemoPaths}. Check tv_enable 1, tv_status, directory permissions and the server's CounterStrikeSharp installation. Reload the map after enabling CSTV.", string.Join(", ", candidatePaths));
				Server.ExecuteCommand("tv_stoprecord");
				ResetVariables();
				confirmationTimer?.Kill();
			}
		}, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
		return HookResult.Stop;
	}

	private void TryAutoRecord()
	{
		if (!Config.AutoRecord.Enabled || mapChangePending || fileName != null)
			return;
		if (!Config.AutoRecord.RecordWarmup && GameRules()?.GameRules?.WarmupPeriod == true)
			return;
		if (PlayerCount() >= Config.AutoRecord.MinPlayerStartRecord)
			// The record listener resolves an omitted name from DefaultFileName.
			Server.ExecuteCommand("tv_record");
	}

	private HookResult CommandListener_StopRecord(CCSPlayerController? player, CommandInfo info)
	{
		if (string.IsNullOrEmpty(fileName))
		{
			ResetVariables();
			return HookResult.Continue;
		}

		try
		{
			// The post hook runs after the engine's stop command; ZipDemoAsync also
			// retries if the final file has not been made available yet.
			ProcessUpload(fileName, recordingPaths, Server.EngineTime - DemoStartTime >= Config.General.MinimumDemoDuration);
		}
		finally
		{
			ResetVariables();
		}

		return HookResult.Continue;
	}

	private void ProcessUpload(string fileName, string[] sourcePaths, bool upload)
	{
		string demoPath = Path.Combine(DemoDirectory, $"{fileName}.dem");
		string zipPath = Path.Combine(DemoDirectory, $"{fileName}.zip");
		var demoLength = TimeSpan.FromSeconds(Server.EngineTime - DemoStartTime);
		string mapName = Server.MapName;
		string serverName = ConVar.Find("hostname")?.StringValue ?? "Unknown Server";
		string roundLabel = (GameRules()?.GameRules?.TotalRoundsPlayed + 1)?.ToString() ?? "Unknown";
		int playerCount = PlayerCount();
		DateTime nowLocal = DateTime.Now;
		string isoTimestamp = DateTime.UtcNow.ToString("o");
		string ftpRemotePath = ReplacePlaceholdersForFileName(Path.Combine(Config.Ftp.RemoteDirectory, Path.GetFileName(zipPath)).Replace("\\", "/"), Path.GetFileName(zipPath));

		var placeholders = new Dictionary<string, string>
		{
			["webhook_name"] = Config.Discord.WebhookName,
			["webhook_avatar"] = Config.Discord.WebhookAvatar,
			["message_text"] = Config.Discord.MessageText,
			["embed_title"] = Config.Discord.EmbedTitle,
			["map"] = mapName,
			["date"] = nowLocal.ToString("yyyy-MM-dd"),
			["time"] = nowLocal.ToString("HH:mm:ss"),
			["timedate"] = nowLocal.ToString("yyyy-MM-dd HH:mm:ss"),
			["length"] = $"{demoLength.Minutes:00}:{demoLength.Seconds:00}",
			["round"] = roundLabel,
			["ftp_link"] = "Not uploaded to FTP.",
			["player_count"] = playerCount.ToString(),
			["server_name"] = serverName,
			["fileName"] = Path.GetFileNameWithoutExtension(fileName),
			["iso_timestamp"] = isoTimestamp,
			["file_size_warning"] = string.Empty,
			["fileSizeInKB"] = "0"
		};

		pendingDemoNames.TryAdd(fileName, 0);
		_ = Task.Run(async () =>
		{
			long fileSizeInBytes = 0;
			try
			{
			    if (!await FileManager.FinalizeDemoAsync(sourcePaths, demoPath, Logger))
			        return;
			    if (!upload)
			        return;
			    if (!await FileManager.ZipDemoAsync(demoPath, zipPath, Logger))
			        return;

			    fileSizeInBytes = new FileInfo(zipPath).Length;
			    long fileSizeInKB = fileSizeInBytes / 1024;
			    placeholders["fileSizeInKB"] = fileSizeInKB.ToString();

			    if (Config.Ftp.Enabled && !string.IsNullOrEmpty(Config.Ftp.Host) && !string.IsNullOrEmpty(Config.Ftp.Username) && !string.IsNullOrEmpty(Config.Ftp.Password) && uploadService != null)
			    {
			        string ftpLink = await uploadService.UploadToFtpAsync(zipPath, ftpRemotePath);
			        placeholders["ftp_link"] = ftpLink;
			        if (Config.Ftp.RetentionEnabled)
			            AppendRetentionRecord(ftpRemotePath);
			    }

			    string payloadTemplatePath = Path.Combine(ModuleDirectory, "payload.json");
			    if (!File.Exists(payloadTemplatePath))
			    {
			        Logger.LogError($"Payload template not found: {payloadTemplatePath}");
			        return;
			    }

			    string payloadTemplate = await File.ReadAllTextAsync(payloadTemplatePath);
			    if (fileSizeInBytes / (1024 * 1024) > maxFileSizeInMB)
			    {
			        Logger.LogWarning($"Zip file size ({fileSizeInBytes / (1024 * 1024)}MB) exceeds Discord's {maxFileSizeInMB}MB limit.");
			        placeholders["file_size_warning"] = $"Warning: File size ({fileSizeInBytes / (1024 * 1024)}MB) exceeds Discord limit. Please use the FTP link.";
			    }

			    string payloadJson = ReplacePlaceholders(payloadTemplate, placeholders);
			    if (!string.IsNullOrWhiteSpace(Config.Discord.WebhookURL))
			    {
			        using var httpClient = new HttpClient();
			        var content = new MultipartFormDataContent
			        {
			            { new StringContent(payloadJson, Encoding.UTF8, "application/json"), "payload_json" }
			        };

			        if (File.Exists(zipPath) && (fileSizeInBytes / (1024 * 1024) <= maxFileSizeInMB) && Config.Discord.WebhookUploadFile)
			        {
			            content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(zipPath)), "file", $"{fileName}.zip");
			        }

			        var response = await httpClient.PostAsync(Config.Discord.WebhookURL, content);
			        response.EnsureSuccessStatusCode();

			        if (Config.General.LogUploads)
			            Logger.LogInformation($"Demo uploaded via Discord: {fileName}");
			    }
			    else if (Config.General.LogUploads)
			    {
			        Logger.LogInformation($"Demo processed (no Discord webhook configured): {fileName}");
			    }

			    if (Config.General.DeleteDemoAfterUpload)
			        await FileManager.DeleteFileAsync(demoPath, Logger, Config.General.LogDeletions);

			    if (Config.General.DeleteZippedDemoAfterUpload)
			        await FileManager.DeleteFileAsync(zipPath, Logger, Config.General.LogDeletions);
			}
			catch (HttpRequestException ex)
			{
			    Logger.LogError($"Error during Discord upload: {ex.Message}");
			}
			catch (Exception ex)
			{
			    Logger.LogError($"Unexpected error in ProcessUpload: {ex.Message}");
			}
			finally
			{
			    pendingDemoNames.TryRemove(fileName, out _);
			}
		});
	}

	private void ResetVariables()
	{
		recordingGeneration++;
		forwardedRecordPath = null;
		recordingPaths = [];
		DemoStartTime = 0.0;
		fileName = null;
	}

	private bool IsDemoInUse(string path)
	{
		string name = Path.GetFileNameWithoutExtension(path);
		return name == fileName || pendingDemoNames.ContainsKey(name);
	}

	private static string ReplacePlaceholders(string input, Dictionary<string, string> placeholders)
	{
		foreach (var kv in placeholders)
		{
			// Replace newlines in placeholder values with escaped newlines for Discord JSON
			string value = kv.Value.Replace("\r\n", "\\n").Replace("\n", "\\n");
			input = input.Replace($"{{{kv.Key}}}", value);
		}

		return input;
	}

	private static string ReplacePlaceholdersForFileName(string pattern, string baseFileName)
	{
		var placeholders = new Dictionary<string, string>
		{
			["fileName"] = baseFileName,
			["map"] = Server.MapName,
			["date"] = DateTime.Now.ToString("yyyy-MM-dd"),
			["time"] = DateTime.Now.ToString("HH-mm-ss"),
			["timestamp"] = DateTime.Now.ToString("yyyyMMdd_HHmmss"),
			["round"] = (GameRules()?.GameRules?.TotalRoundsPlayed + 1)?.ToString() ?? "Unknown",
			["playerCount"] = PlayerCount().ToString()
		};
		return ReplacePlaceholders(pattern, placeholders);
	}


	public void OnConfigParsed(PluginConfig config)
	{
		if (config.Version < Config.Version)
			Logger.LogWarning("Config version mismatch (Expected: {0} | Current: {1})", Config.Version, config.Version);

		if (string.IsNullOrWhiteSpace(config.General.DemoDirectory))
		{
			Logger.LogWarning("DemoDirectory is empty, using default 'discord_demos'");
			config.General.DemoDirectory = "discord_demos";
		}

		EnsureDemoDirectory(config);


		if (config.AutoRecord.CropRounds && !config.AutoRecord.Enabled)
			Logger.LogWarning("AutoRecord.CropRounds enabled but AutoRecord is disabled. CropRounds will not work.");

		if (string.IsNullOrEmpty(config.Discord.WebhookURL))
			Logger.LogWarning("Discord.WebhookURL is not set. Discord upload will be skipped.");


		if (config.AutoRecord.StopOnIdle && config.AutoRecord.IdleTimeSeconds <= 0)
			Logger.LogWarning("AutoRecord.IdleTimeSeconds must be greater than 0 when StopOnIdle is enabled.");

		if (config.AutoRecord.MinPlayerStartRecord < 1)
		{
			Logger.LogWarning("AutoRecord.MinPlayerStartRecord must be at least 1. Using 1.");
			config.AutoRecord.MinPlayerStartRecord = 1;
		}

		this.Config = config;
	}

	private void EnsureDemoDirectory(PluginConfig config)
	{
		var resolvedPath = Path.GetFullPath(Path.Combine(CsgoDirectory, config.General.DemoDirectory));

		try
		{
			Directory.CreateDirectory(resolvedPath);
			demoDirectoryPath = resolvedPath;
			return;
		}
		catch (Exception ex)
		{
			Logger.LogError($"Failed to create demo directory: {ex.Message}");
		}

		config.General.DemoDirectory = "discord_demos";
		resolvedPath = Path.Combine(CsgoDirectory, config.General.DemoDirectory);

		try
		{
			Directory.CreateDirectory(resolvedPath);
		}
		catch (Exception fallbackEx)
		{
			Logger.LogError($"Fallback demo directory creation failed: {fallbackEx.Message}");
		}

		demoDirectoryPath = resolvedPath;
	}
	private List<UploadRetentionRecord> LoadRetentionRecords()
	{
		if (!File.Exists(RetentionFilePath))
			return [];

		var json = File.ReadAllText(RetentionFilePath);
		return JsonSerializer.Deserialize<List<UploadRetentionRecord>>(json) ?? new List<UploadRetentionRecord>();
	}

	private void SaveRetentionRecords(List<UploadRetentionRecord> records)
	{
		var json = JsonSerializer.Serialize(records);
		File.WriteAllText(RetentionFilePath, json);
	}

	private void AppendRetentionRecord(string identifier)
	{
		var records = LoadRetentionRecords();
		records.Add(new UploadRetentionRecord(identifier, DateTime.Now));
		SaveRetentionRecords(records);
	}

	private async Task CleanFtpRetention()
	{
		var records = LoadRetentionRecords();
		var currentTime = DateTime.Now;
		var recordsToRemove = new List<UploadRetentionRecord>();

		using var ftpClient = new AsyncFtpClient(Config.Ftp.Host, Config.Ftp.Username, Config.Ftp.Password, Config.Ftp.Port);

		ftpClient.Config.EncryptionMode = Config.Ftp.UseSftp ? FtpEncryptionMode.Implicit : FtpEncryptionMode.None;
		ftpClient.Config.ValidateAnyCertificate = true;

		await ftpClient.AutoConnect();

		var expiredRecords = records.Where(r => (currentTime - r.UploadedAt).TotalHours >= Config.Ftp.RetentionHours).ToList();

		foreach (var record in expiredRecords)
		{
			try
			{
				await ftpClient.DeleteFile(record.Identifier);
				recordsToRemove.Add(record);

				if (Config.General.LogDeletions)
					Logger.LogInformation($"Deleted FTP file {record.Identifier} due to retention policy.");
			}
			catch (Exception ex)
			{
				Logger.LogError($"Error deleting FTP file {record.Identifier}: {ex.Message}");
			}
		}

		await ftpClient.Disconnect();

		if (recordsToRemove.Count != 0)
		{
			SaveRetentionRecords(records.Except(recordsToRemove).ToList());
		}
	}

	private void AnnounceRecordingStart()
	{
		if (string.IsNullOrEmpty(fileName))
			return;

		foreach (var target in Utilities.GetPlayers())
		{
			if (target?.IsValid != true || target.IsBot || target.IsHLTV)
				continue;

			string prefix = Localizer?.ForPlayer(target, "k4.general.prefix") ?? "{silver}[K4-GOTV]";
			string message = Localizer?.ForPlayer(target, "k4.demo.start", fileName) ?? $"Recording started for demo {fileName}";
			target.PrintToChat($"{prefix} {message}");
		}
	}



	public static int PlayerCount()
		=> Utilities.GetPlayers().Count(p => p?.IsValid == true && !p.IsBot && !p.IsHLTV);

	public static CCSGameRulesProxy? GameRules()
		=> Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
}




















