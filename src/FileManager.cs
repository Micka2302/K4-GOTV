using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace K4GOTV;

public static class FileManager
{
	public static async Task<bool> FinalizeDemoAsync(string[] sourcePaths, string destination, ILogger logger)
	{
		// HLTVServerAsync may finish writing after tv_stoprecord returns.
		// Require stable size/time as well as an exclusive handle before moving.
		(string Path, long Length, DateTime Modified)? previous = null;
		int stableChecks = 0;
		for (int attempt = 0; attempt < 30; attempt++)
		{
			await Task.Delay(500);
			try
			{
				string? source = sourcePaths.FirstOrDefault(File.Exists);
				if (source == null)
					continue;
				var info = new FileInfo(source);
				var current = (source, info.Length, info.LastWriteTimeUtc);
				stableChecks = previous == current && info.Length > 0 ? stableChecks + 1 : 0;
				previous = current;
				if (stableChecks < 3)
					continue;
				using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None)) { }
				if (!Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.Ordinal))
					File.Move(source, destination, overwrite: false);
				logger.LogInformation("Demo finalized: {DemoPath}", destination);
				return true;
			}
			catch (IOException)
			{
				// Missing, locked or still being finalized: retain the source and retry.
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Could not finalize demo to {DemoPath}. Original recording retained.", destination);
				return false;
			}
		}
		logger.LogError("Could not finalize demo to {DemoPath}. Original recording retained if present. Checked: {SourcePaths}", destination, string.Join(", ", sourcePaths));
		return false;
	}

	public static async Task<bool> ZipDemoAsync(string demoPath, string zipPath, ILogger logger)
	{
		int retryCount = 5;
		int delayMilliseconds = 2000;
		FileStream? demoStream = null;

		while (retryCount > 0 && demoStream == null)
		{
			try
			{
				demoStream = new FileStream(demoPath, FileMode.Open, FileAccess.Read, FileShare.None);
			}
			catch (IOException)
			{
				retryCount--;
				await Task.Delay(delayMilliseconds);
			}
		}

		if (demoStream == null)
		{
			logger.LogError($"Failed to access file: {demoPath}");
			return false;
		}

		using (demoStream)
		{
			if (demoStream.Length == 0)
			{
				logger.LogError("Demo is empty, skipping compression: {DemoPath}", demoPath);
				return false;
			}

			bool createdArchive = false;
			try
			{
				// Keep the same exclusive source handle until copying is complete.
				using var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
				createdArchive = true;
				using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);
				var entry = archive.CreateEntry(Path.GetFileName(demoPath), CompressionLevel.Fastest);
				using var entryStream = entry.Open();
				await demoStream.CopyToAsync(entryStream);
				return true;
			}
			catch (Exception ex)
			{
				logger.LogError($"Error occurred during compression: {ex.Message}");
				if (createdArchive)
				{
					try { File.Delete(zipPath); }
					catch (Exception cleanupEx) { logger.LogWarning(cleanupEx, "Could not remove incomplete archive: {ZipPath}", zipPath); }
				}
				return false;
			}
		}
	}

	public static async Task DeleteFileAsync(string path, ILogger logger, bool logDeletion = true)
	{
		if (!File.Exists(path))
		{
			logger.LogWarning($"File not found for deletion: {path}");
			return;
		}

		int retryCount = 0;
		const int maxRetries = 10;

		while (retryCount < maxRetries)
		{
			try
			{
				// Try to open the file to check if it's locked
				using (FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
				{
					// File is open, we can close the stream now
				}

				// Now that we've confirmed the file is accessible and the stream is closed, delete it
				File.Delete(path);

				if (logDeletion)
					logger.LogInformation($"File successfully deleted: {path}");
				return;
			}
			catch (IOException)
			{
				retryCount++;

				if (retryCount < maxRetries)
				{
					// Incremental delay: 1s, 3s, 5s, 7s, 9s, 11s, 13s, 15s, 17s
					int delayMs = 1000 + (retryCount * 2000);
					await Task.Delay(delayMs);
				}
				else
				{
					logger.LogError($"Failed to delete file after {maxRetries} attempts: {path}");
				}
			}
			catch (Exception ex)
			{
				logger.LogError($"Error occurred while deleting file ({path}): {ex.Message}");
				return;
			}
		}
	}
}
