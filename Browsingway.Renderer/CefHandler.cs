using CefSharp;
using CefSharp.OffScreen;
using System.Reflection;

namespace Browsingway.Renderer;

internal static class CefHandler
{
	public static string RootCachePath { get; private set; } = null!;

	public static void Initialise(string cefAssemblyPath, string cefCacheDir, int parentPid)
	{
		CefSettings settings = new()
		{
			BrowserSubprocessPath = Path.Combine(cefAssemblyPath, "CefSharp.BrowserSubprocess.exe"),
			RootCachePath = cefCacheDir,
#if !DEBUG
			LogSeverity = LogSeverity.Fatal,
#endif
		};
		RootCachePath = settings.RootCachePath;
		settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";

		if (Environment.IsPrivilegedProcess)
		{
			Console.Error.WriteLine(
				"游戏正以特权进程状态运行 (如以管理员运行). 这会极大提高安全风险. 且会削弱CEF本身的安全特性. 请以普通用户身份重新启动游戏");
			settings.CefCommandLineArgs.Add("do-not-de-elevate");
		}

		settings.CefCommandLineArgs["persist-user-preferences"] = "1";
		settings.EnableAudio();
		settings.SetOffScreenRenderingBestPerformanceArgs();
		settings.UserAgentProduct = $"Chrome/{Cef.ChromiumVersion} Browsingway/{Assembly.GetEntryAssembly()?.GetName().Version} (ffxiv_pid {parentPid}; renderer_pid {Environment.ProcessId})";

		settings.RegisterScheme(new CefCustomScheme
		{
			SchemeName = "gamebg",
			SchemeHandlerFactory = new GameBackgroundSchemeHandlerFactory(),
			IsStandard = true,
			IsLocal = false,
			IsCorsEnabled = true,
			IsSecure = true
		});

		Cef.Initialize(settings, false, browserProcessHandler: null);
	}

	public static void Shutdown()
	{
		Cef.Shutdown();
	}
}