using Dalamud.Logging;
using ImGuiNET;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Web;

namespace Browsingway;

internal class Dependency
{
	public readonly string Checksum;
	public readonly string Directory;
	public readonly string Url;
	public readonly string Version;

	public Dependency(string url, string directory, string version, string checksum)
	{
		Directory = directory;
		Url = url;
		Version = version;
		Checksum = checksum;
	}
}

internal class DependencyManager : IDisposable
{
	private const string _downloadDir = "downloads";

	// Per-dependency special-cased progress values
	private const short _depExtracting = -1;
	private const short _depComplete = -2;
	private const short _depFailed = -3;

	private static readonly Dependency[] _dependencies = { new("https://oss.yarukon.me/browsingway/cefsharp-{VERSION}.zip", "cef", "103.0.8+g444ebe7+chromium-103.0.5060.66", "ECFFE5E04C1BC04C256EC509FB4AF2B0E9066D1F4F0B8BA3F537FEF0D30980FB") };
	private readonly string _debugCheckDir;

	private readonly string _dependencyDir;
	private readonly ConcurrentDictionary<string, float> _installProgress = new();
	private Dependency[]? _missingDependencies;
	private ViewMode _viewMode = ViewMode.Hidden;

	public DependencyManager(string pluginDir, string pluginConfigDir)
	{
		_dependencyDir = Path.Join(pluginConfigDir, "dependencies");
		_debugCheckDir = Path.GetDirectoryName(pluginDir) ?? pluginDir;
	}

	public void Dispose() { }

	public event EventHandler? DependenciesReady;

	public void Initialise()
	{
		CheckDependencies();
	}

	private void CheckDependencies()
	{
		_missingDependencies = _dependencies.Where(DependencyMissing).ToArray();
		if (_missingDependencies.Length == 0)
		{
			_viewMode = ViewMode.Hidden;
			DependenciesReady?.Invoke(this, EventArgs.Empty);
		}
		else
		{
			_viewMode = ViewMode.Confirm;
		}
	}

	private bool DependencyMissing(Dependency dependency)
	{
		string versionFilePath = Path.Combine(GetDependencyPath(dependency), "VERSION");

		string versionContents;
		try { versionContents = File.ReadAllText(versionFilePath); }
		catch { return true; }

		return !versionContents.Contains(dependency.Version);
	}

	private void InstallDependencies()
	{
		if (_missingDependencies is null)
		{
			return; // nothing too do
		}

		_viewMode = ViewMode.Installing;
		PluginLog.Log("Installing dependencies...");

		IEnumerable<Task> installTasks = _missingDependencies.Select(InstallDependency);
		Task.WhenAll(installTasks).ContinueWith(task =>
		{
			bool failed = _installProgress.Any(pair => pair.Value == _depFailed);
			_viewMode = failed ? ViewMode.Failed : ViewMode.Complete;
			PluginLog.Log($"Dependency install {_viewMode}.");

			try { Directory.Delete(Path.Combine(_dependencyDir, _downloadDir), true); }
			catch { }
		});
	}

	private async Task InstallDependency(Dependency dependency)
	{
		PluginLog.Log($"Downloading {dependency.Directory} {dependency.Version}");

		// Ensure the downloads dir exists
		string downloadDir = Path.Combine(_dependencyDir, _downloadDir);
		Directory.CreateDirectory(downloadDir);

		// Get the file name we'll download to - if it's already in downloads, it may be corrupt, delete
		string filePath = Path.Combine(downloadDir, $"{dependency.Directory}-{dependency.Version}.zip");
		File.Delete(filePath);

		// Set up the download and kick it off
#pragma warning disable SYSLIB0014
		using WebClient client = new();
#pragma warning restore SYSLIB0014
		client.DownloadProgressChanged += (sender, args) => _installProgress.AddOrUpdate(
			dependency.Directory,
			args.ProgressPercentage,
			(key, oldValue) => Math.Max(oldValue, args.ProgressPercentage));
		await client.DownloadFileTaskAsync(
			dependency.Url.Replace("{VERSION}", HttpUtility.UrlEncode(dependency.Version)),
			filePath);

		// Download complete, mark as extracting
		_installProgress.AddOrUpdate(dependency.Directory, _depExtracting, (key, oldValue) => _depExtracting);

		// Calculate the checksum for the download
		string downloadedChecksum;
		try
		{
			using (SHA256 sha = SHA256.Create())
			using (FileStream stream = new(filePath, FileMode.Open))
			{
				stream.Position = 0;
				byte[] rawHash = sha.ComputeHash(stream);
				StringBuilder builder = new(rawHash.Length);
				for (int i = 0; i < rawHash.Length; i++) { builder.Append($"{rawHash[i]:X2}"); }

				downloadedChecksum = builder.ToString();
			}
		}
		catch
		{
			PluginLog.LogError($"Failed to calculate checksum for {filePath}");
			downloadedChecksum = "FAILED";
		}

		// Make sure the checksum matches
		if (downloadedChecksum != dependency.Checksum)
		{
			PluginLog.LogError($"Mismatched checksum for {filePath}: Got {downloadedChecksum} but expected {dependency.Checksum}");
			_installProgress.AddOrUpdate(dependency.Directory, _depFailed, (key, oldValue) => _depFailed);
			File.Delete(filePath);
			return;
		}

		_installProgress.AddOrUpdate(dependency.Directory, _depComplete, (key, oldValue) => _depComplete);

		// Extract to the destination dir
		string destinationDir = GetDependencyPath(dependency);
		try { Directory.Delete(destinationDir, true); }
		catch { }

		ZipFile.ExtractToDirectory(filePath, destinationDir);

		// Clear out the downloaded file now we're done with it
		File.Delete(filePath);
	}

	public string GetDependencyPathFor(string dependencyDir)
	{
		Dependency? dependency = _dependencies.First(dependency => dependency.Directory == dependencyDir);
		if (dependency == null) { throw new Exception($"Unknown dependency {dependencyDir}"); }

		return GetDependencyPath(dependency);
	}

	private string GetDependencyPath(Dependency dependency)
	{
		string localDebug = Path.Combine(_debugCheckDir, dependency.Directory);
		if (Directory.Exists(localDebug)) { return localDebug; }

		return Path.Combine(_dependencyDir, dependency.Directory);
	}

	public void Render()
	{
		if (_viewMode == ViewMode.Hidden) { return; }

		ImGuiWindowFlags windowFlags = ImGuiWindowFlags.AlwaysAutoResize;
		ImGui.Begin("Browsingway 依赖", windowFlags);
		//ImGui.SetWindowFocus();

		switch (_viewMode)
		{
			case ViewMode.Confirm:
				RenderConfirm();
				break;
			case ViewMode.Installing:
				RenderInstalling();
				break;
			case ViewMode.Complete:
				RenderComplete();
				break;
			case ViewMode.Failed:
				RenderFailed();
				break;
		}

		ImGui.End();
	}

	private void RenderConfirm()
	{
		ImGui.Text("下列依赖未找到:");

		if (_missingDependencies == null) { return; }

		ImGui.Indent();
		foreach (Dependency dependency in _missingDependencies)
		{
			ImGui.Text($"{dependency.Directory} ({dependency.Version})");
		}

		ImGui.Unindent();

		ImGui.Separator();

		if (ImGui.Button("安装丢失的依赖")) { InstallDependencies(); }
	}

	private void RenderInstalling()
	{
		ImGui.Text("正在安装依赖...");

		ImGui.Separator();

		RenderDownloadProgress();
	}

	private void RenderComplete()
	{
		ImGui.Text("依赖安装完毕!");

		ImGui.Separator();

		RenderDownloadProgress();

		ImGui.Separator();

		if (ImGui.Button("确定", new Vector2(100, 0))) { CheckDependencies(); }
	}

	private void RenderFailed()
	{
		ImGui.Text("有1个或多个依赖安装失败.");
		ImGui.Text("这通常是因为网络问题导致的. 请重试.");
		ImGui.Text("如果该问题持续发生, 那就继续重试吧.");

		ImGui.Separator();

		RenderDownloadProgress();

		ImGui.Separator();

		if (ImGui.Button("重试", new Vector2(100, 0))) { CheckDependencies(); }
	}

	private void RenderDownloadProgress()
	{
		Vector2 progressSize = new(200, 0);

		foreach (KeyValuePair<string, float> progress in _installProgress)
		{
			if (progress.Value == _depExtracting) { ImGui.ProgressBar(1, progressSize, "解压"); }
			else if (progress.Value == _depComplete) { ImGui.ProgressBar(1, progressSize, "完毕"); }
			else if (progress.Value == _depFailed)
			{
				ImGui.PushStyleColor(ImGuiCol.PlotHistogram, 0xAA0000FF);
				ImGui.ProgressBar(1, progressSize, "错误");
				ImGui.PopStyleColor();
			}
			else { ImGui.ProgressBar(progress.Value / 100, progressSize); }

			ImGui.SameLine();
			ImGui.Text(progress.Key);
		}
	}

	private enum ViewMode
	{
		Confirm,
		Installing,
		Complete,
		Failed,
		Hidden
	}
}