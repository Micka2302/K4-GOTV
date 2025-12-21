using FluentFTP;
using FluentFTP.Exceptions;
using Microsoft.Extensions.Logging;

namespace K4GOTV;

public class UploadService
{
	private readonly PluginConfig _config;
	private readonly ILogger _logger;

	public UploadService(PluginConfig config, ILogger logger)
	{
		_config = config;
		_logger = logger;
	}

	public async Task<string> UploadToFtpAsync(string filePath, string remoteFilePath)
	{
		using var client = new AsyncFtpClient(_config.Ftp.Host, _config.Ftp.Username, _config.Ftp.Password, _config.Ftp.Port);
		try
		{
			client.Config.EncryptionMode = _config.Ftp.UseSftp ? FtpEncryptionMode.Implicit : FtpEncryptionMode.None;
			client.Config.ValidateAnyCertificate = true;
			await client.AutoConnect();
			await client.UploadFile(filePath, remoteFilePath);
			string protocol = _config.Ftp.UseSftp ? "sftp" : "ftp";
			return $"{protocol}://{_config.Ftp.Host}/{remoteFilePath}";
		}
		catch (FtpException ex)
		{
			_logger.LogError($"FTP upload error: {ex.Message}");
			throw;
		}
		finally
		{
			await client.Disconnect();
		}
	}
}

