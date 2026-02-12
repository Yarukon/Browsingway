using Browsingway.Common.Ipc;
using CefSharp;
using CefSharp.OffScreen;
using CefSharp.Structs;
using BrowserSettings = CefSharp.BrowserSettings;
using RequestContext = CefSharp.RequestContext;
using RequestContextSettings = CefSharp.RequestContextSettings;
using Size = System.Drawing.Size;
using WindowInfo = CefSharp.WindowInfo;

namespace Browsingway.Renderer;

internal class Overlay : IDisposable
{
	private readonly string _id;
	private readonly int _framerate;
	public readonly TextureRenderHandler RenderHandler;
	private ChromiumWebBrowser? _browser;
	private string _url;
	private float _zoom;
	private bool _muted;
	private string _customCss;

	public Overlay(string id, string url, float zoom, bool muted, int framerate, string customCss,
		TextureRenderHandler renderHandler)
	{
		_id = id;
		_url = url;
		_zoom = zoom;
		_framerate = framerate;
		_muted = muted;
		_customCss = customCss;
		RenderHandler = renderHandler;
	}

	public void Dispose()
	{
		RenderHandler.Dispose();

		if (_browser is not null)
		{
			_browser.RenderHandler = null;
			_browser.Dispose();
		}
	}

	public void Initialise()
	{
		var requestContextSettings = new RequestContextSettings
		{
			CachePath = Path.Combine(CefHandler.RootCachePath, _id),
			PersistSessionCookies = true
		};
		var rc = new RequestContext(requestContextSettings);
		// Register gamebg scheme on this context — global registration doesn't propagate to custom contexts
		rc.RegisterSchemeHandlerFactory("gamebg", null, new GameBackgroundSchemeHandlerFactory());

		Console.WriteLine($"Overlay[{_id}] creating browser for URL: {_url}");
		_browser = new ChromiumWebBrowser(_url, automaticallyCreateBrowser: false, requestContext: rc);
		_browser.RenderHandler = RenderHandler;
		_browser.MenuHandler = new CefMenuHandler();
		Rect size = RenderHandler.GetViewRect();

		// General _browser config
		WindowInfo windowInfo = new() {Width = size.Width, Height = size.Height};
		windowInfo.SetAsWindowless(IntPtr.Zero);

		// WindowInfo gets ignored sometimes, be super sure:
		_browser.BrowserInitialized += (_, _) =>
		{
			Console.WriteLine($"Overlay[{_id}] BrowserInitialized, setting size to {size.Width}x{size.Height}");
			_browser.Size = new Size(size.Width, size.Height);
			Mute(_muted);
		};

		_browser.LoadingStateChanged += (_, args) =>
		{
			Console.WriteLine($"Overlay[{_id}] LoadingStateChanged: IsLoading={args.IsLoading}");
			if (!args.IsLoading)
			{
				_browser.SetZoomLevel(ScaleZoomLevel(_zoom));
				InjectUserCss(_customCss);
				InjectGameBackgroundCanvas();
			}
		};

		BrowserSettings browserSettings = new() {WindowlessFrameRate = _framerate};

		// Ready, boot up the _browser
		_browser.CreateBrowser(windowInfo, browserSettings);
		Console.WriteLine($"Overlay[{_id}] CreateBrowser called");

		browserSettings.Dispose();
		windowInfo.Dispose();
	}

	public void InjectUserCss(string css)
	{
		if (css.Length == 0 && _customCss.Length == 0)
			return; // nothing to do

		_customCss = css; // to reapply correctly on load

		// escape rules
		// ` -> \` to prevent end of string
		// ${ -> \${ to prevent variable injection
		// Using a template string (``) instead of a quoted string ('') to not have to deal with javascript
		// newline weirdness (plus it behaves a bit like a verbatim string)
		css = css.Replace("`", @"\'");
		css = css.Replace("${", @"\${");

		// (()=>{...})() self executable function to prevent scope issues
		_browser.GetMainFrame().ExecuteJavaScriptAsync(
			"(()=>{const style = document.getElementById('user-css') ?? document.createElement('style');"
			+ "style.id = 'user-css'; style.textContent =`" + css + " `;document.head.append(style);})()");
	}

	private void InjectGameBackgroundCanvas()
	{
		int bufferId = RenderHandler.FrameBufferId;
		_browser?.GetMainFrame()?.ExecuteJavaScriptAsync(
			"(()=>{" +
			"if(document.getElementById('bw-game-bg-canvas'))return;" +
			"const c=document.createElement('canvas');" +
			"c.id='bw-game-bg-canvas';" +
			"c.style.cssText='position:fixed;top:0;left:0;width:100%;height:100%;z-index:-2147483647;pointer-events:none;';" +
			"document.documentElement.appendChild(c);" +
			"const ctx=c.getContext('2d');" +
			"const img=new Image();" +
			"img.crossOrigin='anonymous';" +
			"function update(){" +
			"img.src='';" +
			"img.src='gamebg://localhost/frame?id=" + bufferId + "&t='+Date.now();" +
			"}" +
			"img.onload=function(){" +
			"if(img.naturalWidth>0&&img.naturalHeight>0){" +
			"c.width=img.naturalWidth;c.height=img.naturalHeight;" +
			"ctx.drawImage(img,0,0);" +
			"}" +
			"setTimeout(update,16);" +
			"};" +
			"img.onerror=function(){" +
			"setTimeout(update,50);" +
			"};" +
			"update();" +
			"})();"
		);
	}

	public void Navigate(string newUrl)
	{
		// If navigating to the same _url, force a clean reload
		if (_browser?.Address == newUrl)
		{
			_browser.Reload(true);
			return;
		}

		// Otherwise load regularly
		_url = newUrl;
		_browser?.Load(newUrl);
	}

	public void Zoom(float zoom)
	{
		_zoom = zoom;
		_browser?.SetZoomLevel(ScaleZoomLevel(zoom));
	}

	public void Mute(bool mute)
	{
		_muted = mute;
		_browser?.GetBrowserHost().SetAudioMuted(mute);
	}

	public void Debug()
	{
		_browser.ShowDevTools();
	}

	public void HandleMouseEvent(MouseButtonMessage msg)
	{
		// If the _browser isn't ready yet, noop
		if (_browser == null || !_browser.IsBrowserInitialized) { return; }

		var cursor = DpiScaling.ScaleViewPoint(msg.X, msg.Y);

		// Update the renderer's concept of the mouse cursor
		RenderHandler.SetMousePosition(cursor.X, cursor.Y);

		MouseEvent evt = new(cursor.X, cursor.Y, DecodeInputModifier(msg.Modifier));

		IBrowserHost? host = _browser.GetBrowserHost();

		// Ensure the mouse position is up to date
		host.SendMouseMoveEvent(evt, msg.Leaving);

		// Fire any relevant click events
		List<MouseButtonType> doubleClicks = DecodeMouseButtons(msg.Double);
		DecodeMouseButtons(msg.Down)
			.ForEach(button => host.SendMouseClickEvent(evt, button, false, doubleClicks.Contains(button) ? 2 : 1));
		DecodeMouseButtons(msg.Up).ForEach(button => host.SendMouseClickEvent(evt, button, true, 1));

		// CEF treats the wheel delta as mode 0, pixels. Bump up the numbers to match typical in-_browser experience.
		int deltaMult = 100;
		host.SendMouseWheelEvent(evt, (int)msg.WheelX * deltaMult, (int)msg.WheelY * deltaMult);
	}

	public void HandleKeyEvent(KeyEventMessage request)
	{
		_browser.GetBrowserHost().SendKeyEvent(request.Msg, request.WParam, request.LParam);
	}

	public void Resize(Size size)
	{
		// Need to resize renderer first, the _browser will check it (and hence the texture) when _browser.Size is set.
		RenderHandler.Resize(size);
		if (_browser is not null)
		{
			_browser.Size = size;
		}
	}

	private List<MouseButtonType> DecodeMouseButtons(MouseButton buttons)
	{
		List<MouseButtonType> result = new();
		if ((buttons & MouseButton.Primary) == MouseButton.Primary) { result.Add(MouseButtonType.Left); }

		if ((buttons & MouseButton.Secondary) == MouseButton.Secondary) { result.Add(MouseButtonType.Right); }

		if ((buttons & MouseButton.Tertiary) == MouseButton.Tertiary) { result.Add(MouseButtonType.Middle); }

		return result;
	}

	private CefEventFlags DecodeInputModifier(InputModifier modifier)
	{
		CefEventFlags result = CefEventFlags.None;
		if ((modifier & InputModifier.Shift) == InputModifier.Shift) { result |= CefEventFlags.ShiftDown; }

		if ((modifier & InputModifier.Control) == InputModifier.Control) { result |= CefEventFlags.ControlDown; }

		if ((modifier & InputModifier.Alt) == InputModifier.Alt) { result |= CefEventFlags.AltDown; }

		return result;
	}

	private double ScaleZoomLevel(float zoom)
	{
		if (Math.Abs(zoom - 100f) < 0.5f)
		{
			return 0;
		}

		return (5.46149645 * Math.Log(_zoom)) - 25.12;
	}
}