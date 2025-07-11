using Dalamud.Interface.Internal;
using Dalamud.Interface.Textures;
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

public class DependencyManager : IDisposable
{
	private const string _downloadDir = "downloads";

	private const uint _colorProgress = 0xAAD76B39;
	private const uint _colorError = 0xAA0000FF;
	private const uint _colorDone = 0xAA355506;

	// Per-dependency special-cased progress values
	private const short _depExtracting = -1;
	private const short _depComplete = -2;
	private const short _depFailed = -3;

	private static readonly Dependency[] _dependencies = { new("https://oss.yarukon.me/browsingway/cefsharp-{VERSION}.zip", "cef", "134.3.9+g5dc6f2f+chromium-134.0.6998.178", "F761372E54962FBF1F8906EE864F8B92D3A3A5B4F5EA5C34EA12340907E0B41A") };
	private readonly string _debugCheckDir;

	private readonly string _dependencyDir;
	private readonly ConcurrentDictionary<string, float> _installProgress = new();
	private Dependency[]? _missingDependencies;
	private ViewMode _viewMode = ViewMode.Hidden;
	private ISharedImmediateTexture? _texIcon;

	public DependencyManager(string pluginDir, string pluginConfigDir)
	{
		_dependencyDir = Path.Join(pluginConfigDir, "dependencies");
		_debugCheckDir = Path.GetDirectoryName(pluginDir) ?? pluginDir;
		_texIcon = Services.TextureProvider.GetFromFile(Path.Combine(pluginDir, "icon.png"));
	}

	public void Dispose() { }

	public event EventHandler? DependenciesReady;

	public void Initialise()
	{
		CheckDependencies();
	}

	private void CheckDependencies()
	{
		_missingDependencies = [.. _dependencies.Where(DependencyMissing)];
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
		Services.PluginLog.Info("正在安装依赖...");

		IEnumerable<Task> installTasks = _missingDependencies.Select(InstallDependency);
		Task.WhenAll(installTasks).ContinueWith(task =>
		{
			bool failed = _installProgress.Any(pair => pair.Value == _depFailed);
			_viewMode = failed ? ViewMode.Failed : ViewMode.Complete;
			Services.PluginLog.Info($"Dependency install {_viewMode}.");

			try { Directory.Delete(Path.Combine(_dependencyDir, _downloadDir), true); }
			catch { }
		});
	}

	private async Task InstallDependency(Dependency dependency)
	{
		Services.PluginLog.Info($"Downloading {dependency.Directory} {dependency.Version}");

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
			Services.PluginLog.Error($"检查文件校验和 {filePath} 失败");
			downloadedChecksum = "FAILED";
		}

		// Make sure the checksum matches
		if (downloadedChecksum != dependency.Checksum)
		{
			Services.PluginLog.Error(
				$"不匹配的校验和 {filePath}: 返回 {downloadedChecksum} 但预期为 {dependency.Checksum}");
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

		ImGui.SetNextWindowSize(new Vector2(1300, 350), ImGuiCond.Always);
		ImGuiWindowFlags windowFlags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize;
		ImGui.Begin("Browsingway 依赖", windowFlags);
		if (_texIcon is not null)
			ImGui.Image(_texIcon.GetWrapOrEmpty().ImGuiHandle, new Vector2(256, 256));

		ImGui.SameLine();

		string version = _missingDependencies?.First()?.Version ?? "???";
		string checksum = _missingDependencies?.First()?.Checksum ?? "???";
		ImGui.Text("Browsingway 需要额外的依赖才能正常运行.\n" +
		           "因依赖文件过大而没有随插件附带.\n\n" +
		           "依赖文件托管于Github并进行了SHA256校验和:\n" +
		           "https://github.com/Styr1x/Browsingway/releases/tag/cef-binaries\n\n" +
		           "CefSharp: " + version + "\n" +
		           "SHA256: " + checksum
		);
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
		if (_missingDependencies == null) { return; }

		ImGui.Separator();
		if (ImGui.Button("安装丢失的依赖")) { InstallDependencies(); }
	}

	private void RenderInstalling()
	{
		ImGui.Text("正在安装依赖: ");
		ImGui.SameLine();
		RenderDownloadProgress();
	}

	private void RenderComplete()
	{
		ImGui.Text("正在安装依赖: ");
		ImGui.SameLine();
		RenderDownloadProgress();
		ImGui.SameLine();
		if (ImGui.Button("关闭", new Vector2(100, 0))) { CheckDependencies(); }
	}

	private void RenderFailed()
	{
		ImGui.Text("正在安装依赖: ");
		ImGui.SameLine();
		RenderDownloadProgress();
		ImGui.SameLine();
		if (ImGui.Button("重试", new Vector2(100, 0))) { CheckDependencies(); }
	}

	private void RenderDownloadProgress()
	{
		Vector2 progressSize = new(875, 0);

		foreach (KeyValuePair<string, float> progress in _installProgress)
		{
			if (progress.Value == _depExtracting)
			{
				ImGui.PushStyleColor(ImGuiCol.PlotHistogram, _colorProgress);
				ImGui.ProgressBar(1, progressSize, "正在解压");
				ImGui.PopStyleColor();
			}
			else if (progress.Value == _depComplete)
			{
				ImGui.PushStyleColor(ImGuiCol.PlotHistogram, _colorDone);
				ImGui.ProgressBar(1, progressSize, "完成");
				ImGui.PopStyleColor();
			}
			else if (progress.Value == _depFailed)
			{
				ImGui.PushStyleColor(ImGuiCol.PlotHistogram, _colorError);
				ImGui.ProgressBar(1, progressSize, "错误");
				ImGui.PopStyleColor();
			}
			else
			{
				ImGui.PushStyleColor(ImGuiCol.PlotHistogram, _colorProgress);
				ImGui.ProgressBar(progress.Value / 100, progressSize);
				ImGui.PopStyleColor();
			}
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