using Browsingway.Common.Ipc;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Range = CefSharp.Structs.Range;
using Size = System.Drawing.Size;

namespace Browsingway.Renderer;

internal unsafe class TextureRenderHandler : IRenderHandler
{
	// Global lock for D3D11 immediate context — not thread-safe across overlays
	private static readonly object _d3dLock = new();

	// CEF buffers are 32-bit BGRA
	private const byte _bytesPerPixel = 4;

	// TODO: replace with lockless implementation
	private readonly object _renderLock = new();

	// TODO: remove me
	private byte[] _alphaLookupBuffer = Array.Empty<byte>();
	private int _alphaLookupBufferHeight;
	private int _alphaLookupBufferWidth;

	private Cursor _cursor;

	// Transparent background click-through state
	private bool _cursorOnBackground;

	private ConcurrentBag<IntPtr> _obsoleteTextures = [];

	private Rect _popupRect;
	private ID3D11Texture2D* _popupTexture;
	private bool _popupVisible;
	private ID3D11Texture2D* _sharedTexture;

	private IntPtr _sharedTextureHandle = IntPtr.Zero;
	private ID3D11Texture2D* _viewTexture;

	// Game background readback
	private ID3D11Texture2D* _gameBackgroundTexture;
	private ID3D11Texture2D* _stagingTexture;
	private int _stagingWidth;
	private int _stagingHeight;
	private DXGI_FORMAT _stagingFormat;
	private Timer? _readbackTimer;
	private bool _hasGameBackground;
	private IntPtr _cachedGameBgHandle;
	private int _openSharedFailCount;
	private bool _disposed;
	private bool _deviceLost;
	private bool _firstPaintLogged;
	private int _readbackCallCount;
	private int _readbackSuccessCount;
	private readonly int _frameBufferId;
	private readonly GameBackgroundFrameBuffer _frameBuffer;

	public int FrameBufferId => _frameBufferId;

	public TextureRenderHandler(Size size)
	{
		_frameBufferId = GameBackgroundFrameBuffer.CreateBuffer();
		_frameBuffer = GameBackgroundFrameBuffer.Get(_frameBufferId)!;
		_sharedTexture = BuildViewTexture(size, true);
		_viewTexture = BuildViewTexture(size, false);
	}

	public IntPtr SharedTextureHandle
	{
		get
		{
			if (_sharedTextureHandle == IntPtr.Zero)
			{
				IDXGIResource* resource;
				Guid resourceGuid = typeof(IDXGIResource).GUID;
				HRESULT hr = ((IUnknown*)_sharedTexture)->QueryInterface(&resourceGuid, (void**)&resource);
				if (hr.SUCCEEDED)
				{
					HANDLE sharedHandle;
					resource->GetSharedHandle(&sharedHandle);
					_sharedTextureHandle = (IntPtr)sharedHandle.Value;
					resource->Release();
				}
			}

			return _sharedTextureHandle;
		}
	}

	public event EventHandler<Cursor>? CursorChanged;

	public void Dispose()
	{
		_disposed = true;

		// Stop the timer first, then wait for any in-flight callback to finish
		_readbackTimer?.Dispose();
		_readbackTimer = null;

		// Acquire the D3D lock to ensure no timer callback is mid-execution
		lock (_d3dLock)
		{
			ReleaseGameBackgroundResources();

			_sharedTexture->Release();
			_viewTexture->Release();
			if (_popupTexture != null)
			{
				_popupTexture->Release();
			}

			foreach (IntPtr texturePtr in _obsoleteTextures)
			{
				((ID3D11Texture2D*)texturePtr)->Release();
			}
		}

		GameBackgroundFrameBuffer.RemoveBuffer(_frameBufferId);
	}

	public Rect GetViewRect()
	{
		// There's a very small chance that OnPaint's cleanup will delete the current _sharedTexture midway through this function -
		// Try a few times just in case before failing out with an obviously-wrong value
		// hi adam
		// TODO: proper threading model instead of shitty hacks
		for (int i = 0; i < 5; i++)
		{
			try { return GetViewRectInternal(); }
			catch (NullReferenceException) { }
		}

		return new Rect(0, 0, 1, 1);
	}

	public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo)
	{
		// TODO: use this instead of manual texture copying
		throw new NotImplementedException();
	}

	public void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
	{
		lock (_d3dLock)
		{
			if (_deviceLost || DxHandler.Device == null)
			{
				Console.Error.WriteLine("OnPaint skipped: device is lost or null");
				return;
			}

			// Check device health before any D3D operations
			HRESULT deviceHr = DxHandler.Device->GetDeviceRemovedReason();
			if (deviceHr.FAILED)
			{
				Console.Error.WriteLine($"OnPaint: D3D device removed (reason={deviceHr}), stopping all rendering");
				_deviceLost = true;
				ReleaseGameBackgroundResources();
				return;
			}

			ID3D11Texture2D* targetTexture = type switch
			{
				PaintElementType.View => _viewTexture,
				PaintElementType.Popup => _popupTexture,
				_ => throw new Exception($"Unknown paint type {type}")
			};

			if (!_firstPaintLogged)
			{
				_firstPaintLogged = true;
				Console.WriteLine($"OnPaint first call: type={type} dirty=({dirtyRect.X},{dirtyRect.Y},{dirtyRect.Width},{dirtyRect.Height}) buffer={width}x{height}");
			}

			// keep buffer to make alpha checks later on.
			// TODO: make this a back and front buffer to atomic swap them
			if (type == PaintElementType.View)
			{
				// check if lookup buffer is big enough
				int requiredBufferSize = width * height * _bytesPerPixel;
				_alphaLookupBufferWidth = width;
				_alphaLookupBufferHeight = height;
				if (_alphaLookupBuffer.Length < requiredBufferSize)
				{
					_alphaLookupBuffer = new byte[width * height * _bytesPerPixel];
				}

				fixed (void* dstBuffer = _alphaLookupBuffer)
				{
					Buffer.MemoryCopy(buffer.ToPointer(), dstBuffer, _alphaLookupBuffer.Length, requiredBufferSize);
				}
			}

			// Calculate offset multipliers for the current buffer
			int rowPitch = width * _bytesPerPixel;
			int depthPitch = rowPitch * height;

			// Build the destination region for the dirty rect that we'll draw to
			D3D11_TEXTURE2D_DESC texDesc;
			targetTexture->GetDesc(&texDesc);
			IntPtr sourceRegionPtr = buffer + (dirtyRect.X * _bytesPerPixel) + (dirtyRect.Y * rowPitch);
			D3D11_BOX destinationBox = new()
			{
				top = (uint)Math.Min(dirtyRect.Y, (int)texDesc.Height),
				bottom = (uint)Math.Min(dirtyRect.Y + dirtyRect.Height, (int)texDesc.Height),
				left = (uint)Math.Min(dirtyRect.X, (int)texDesc.Width),
				right = (uint)Math.Min(dirtyRect.X + dirtyRect.Width, (int)texDesc.Width),
				front = 0,
				back = 1
			};

			// Draw to the target
			ID3D11DeviceContext* context;
			DxHandler.Device->GetImmediateContext(&context);

			context->UpdateSubresource(
				(ID3D11Resource*)targetTexture,
				0,
				&destinationBox,
				sourceRegionPtr.ToPointer(),
				(uint)rowPitch,
				(uint)depthPitch);

			// composite final picture
			// draw view layer, first
			context->CopySubresourceRegion(
				(ID3D11Resource*)_sharedTexture,
				0,
				0,
				0,
				0,
				(ID3D11Resource*)_viewTexture,
				0,
				null);

			// draw popup layer if required
			if (_popupVisible && _popupTexture != null)
			{
				Point popupPos = DpiScaling.ScaleScreenPoint(_popupRect.X, _popupRect.Y);
				context->CopySubresourceRegion(
					(ID3D11Resource*)_sharedTexture,
					0,
					(uint)popupPos.X,
					(uint)popupPos.Y,
					0,
					(ID3D11Resource*)_popupTexture,
					0,
					null);
			}

			context->Flush();
			context->Release();

			// Rendering is complete, clean up any obsolete textures
			ConcurrentBag<IntPtr> textures = _obsoleteTextures;
			_obsoleteTextures = [];
			foreach (IntPtr texPtr in textures)
			{
				((ID3D11Texture2D*)texPtr)->Release();
			}
		}
	}

	public void OnPopupShow(bool show)
	{
		_popupVisible = show;
	}

	public void OnPopupSize(Rect rect)
	{
		_popupRect = DpiScaling.ScaleScreenRect(rect);

		// I'm really not sure if this happens. If it does, frequently - will probably need 2x shared textures and some jazz.
		D3D11_TEXTURE2D_DESC texDesc;
		_sharedTexture->GetDesc(&texDesc);
		if (_popupRect.Width > texDesc.Width || _popupRect.Height > texDesc.Height)
		{
			Console.Error.WriteLine(
				$"Trying to build popup layer ({_popupRect.Width}x{_popupRect.Height}) larger than primary surface ({texDesc.Width}x{texDesc.Height}).");
		}

		// Get a reference to the old _sharedTexture, we'll make sure to assign a new _sharedTexture before disposing the old one.
		ID3D11Texture2D* oldTexture = _popupTexture;

		// Build a _sharedTexture for the new sized popup
		_popupTexture = BuildViewTexture(new Size(_popupRect.Width, _popupRect.Height), false);

		if (oldTexture != null)
		{
			oldTexture->Release();
		}
	}

	public ScreenInfo? GetScreenInfo()
	{
		return new ScreenInfo {DeviceScaleFactor = DpiScaling.GetDeviceScale()};
	}

	public bool GetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
	{
		screenX = viewX;
		screenY = viewY;

		return false;
	}

	public void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode)
	{
	}

	public void OnImeCompositionRangeChanged(Range selectedRange, Rect[] characterBounds)
	{
	}

	public void OnCursorChange(IntPtr cursorPtr, CursorType type, CursorInfo customCursorInfo)
	{
		_cursor = EncodeCursor(type);

		// If we're on background, don't flag a cursor change
		if (!_cursorOnBackground) { CursorChanged?.Invoke(this, _cursor); }
	}

	public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y)
	{
		// Returning false to abort drag operations.
		return false;
	}

	public void UpdateDragCursor(DragOperationsMask operation)
	{
	}

	public void SetGameBackground(IntPtr sharedHandle, int width, int height)
	{
		lock (_d3dLock)
		{
			if (_disposed || _deviceLost || DxHandler.Device == null || sharedHandle == IntPtr.Zero || width <= 0 || height <= 0)
			{
				Console.Error.WriteLine($"SetGameBackground[buf={_frameBufferId}] skipped: disposed={_disposed} deviceLost={_deviceLost} device={DxHandler.Device != null} handle=0x{sharedHandle:X} size={width}x{height}");
				ReleaseGameBackgroundResources();
				_hasGameBackground = false;
				_cachedGameBgHandle = IntPtr.Zero;
				_openSharedFailCount = 0;
				return;
			}

			// Skip if handle hasn't changed
			if (sharedHandle == _cachedGameBgHandle && _hasGameBackground)
				return;

			// If handle changed, reset fail count
			if (sharedHandle != _cachedGameBgHandle)
				_openSharedFailCount = 0;

			// Stop retrying after repeated failures with the same handle
			if (_openSharedFailCount >= 3)
				return;

			// Check device health before opening shared resource
			HRESULT deviceHr = DxHandler.Device->GetDeviceRemovedReason();
			if (deviceHr.FAILED)
			{
				Console.Error.WriteLine($"SetGameBackground: device already removed (reason={deviceHr})");
				_deviceLost = true;
				return;
			}

			// Release old game background resources
			ReleaseGameBackgroundResources();
			_cachedGameBgHandle = sharedHandle;

			// Open the shared texture from the plugin process
			Guid texture2DGuid = typeof(ID3D11Texture2D).GUID;
			void* texturePtr;
			HRESULT hr = DxHandler.Device->OpenSharedResource((HANDLE)sharedHandle, &texture2DGuid, &texturePtr);
			if (hr.FAILED)
			{
				_openSharedFailCount++;
				if (_openSharedFailCount <= 3)
					Console.Error.WriteLine($"Failed to open game background shared texture: {hr} (handle=0x{sharedHandle:X}, size={width}x{height}, attempt={_openSharedFailCount})");
				_hasGameBackground = false;
				return;
			}

			_gameBackgroundTexture = (ID3D11Texture2D*)texturePtr;

			// Create staging texture matching the game texture format
			D3D11_TEXTURE2D_DESC texDesc;
			_gameBackgroundTexture->GetDesc(&texDesc);
			Console.WriteLine($"Game background texture opened: {texDesc.Width}x{texDesc.Height} format={texDesc.Format} handle=0x{sharedHandle:X}");
			EnsureStagingTexture((int)texDesc.Width, (int)texDesc.Height, texDesc.Format);

			if (_stagingTexture == null)
			{
				Console.Error.WriteLine("Staging texture creation failed, aborting game background setup.");
				ReleaseGameBackgroundResources();
				_hasGameBackground = false;
				return;
			}

			_hasGameBackground = true;
			_openSharedFailCount = 0;
			Console.WriteLine($"Game background readback timer started for frameBufferId={_frameBufferId}.");
			StartReadbackTimer();
		}
	}

	private void EnsureStagingTexture(int width, int height, DXGI_FORMAT format)
	{
		if (_stagingTexture != null && _stagingWidth == width && _stagingHeight == height && _stagingFormat == format)
			return;

		ReleaseStagingTexture();

		D3D11_TEXTURE2D_DESC desc = new()
		{
			Width = (uint)width,
			Height = (uint)height,
			MipLevels = 1,
			ArraySize = 1,
			Format = format,
			SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
			Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
			BindFlags = 0,
			CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
			MiscFlags = 0
		};

		ID3D11Texture2D* staging;
		HRESULT hr = DxHandler.Device->CreateTexture2D(&desc, null, &staging);
		if (hr.FAILED)
		{
			Console.Error.WriteLine($"Failed to create staging texture: {hr}");
			return;
		}

		_stagingTexture = staging;
		_stagingWidth = width;
		_stagingHeight = height;
		_stagingFormat = format;
	}

	private void StartReadbackTimer()
	{
		if (_readbackTimer != null) return;
		// ~60fps readback to minimize latency
		_readbackTimer = new Timer(ReadbackTimerCallback, null, 0, 16);
	}

	private void StopReadbackTimer()
	{
		_readbackTimer?.Dispose();
		_readbackTimer = null;
	}

	private void ReadbackTimerCallback(object? state)
	{
		int callNum = Interlocked.Increment(ref _readbackCallCount);

		if (_disposed || !_hasGameBackground || _deviceLost)
		{
			if (callNum <= 3)
				Console.Error.WriteLine($"ReadbackTimer[buf={_frameBufferId}] early exit: disposed={_disposed} hasGameBg={_hasGameBackground} deviceLost={_deviceLost}");
			return;
		}

		if (callNum <= 3)
			Console.WriteLine($"ReadbackTimer[buf={_frameBufferId}] callback #{callNum}, waiting for lock...");

		try
		{
			lock (_d3dLock)
			{
				if (_disposed || !_hasGameBackground || _deviceLost || _gameBackgroundTexture == null || _stagingTexture == null)
				{
					if (callNum <= 3)
						Console.Error.WriteLine($"ReadbackTimer[buf={_frameBufferId}] inner check failed: gameBgTex={(_gameBackgroundTexture != null)} stagingTex={(_stagingTexture != null)}");
					return;
				}

				if (DxHandler.Device == null)
					return;

				// Check if device is still healthy BEFORE any GPU work
				HRESULT deviceHr = DxHandler.Device->GetDeviceRemovedReason();
				if (deviceHr.FAILED)
				{
					Console.Error.WriteLine($"ReadbackTimer: D3D device already removed before CopyResource (reason={deviceHr})");
					_deviceLost = true;
					_hasGameBackground = false;
					StopReadbackTimer();
					return;
				}

				ID3D11DeviceContext* context;
				DxHandler.Device->GetImmediateContext(&context);

				// GPU copy: game texture → staging texture
				context->CopyResource((ID3D11Resource*)_stagingTexture, (ID3D11Resource*)_gameBackgroundTexture);
				context->Flush();

				// Check device health AFTER CopyResource — this is where stale textures kill the device
				deviceHr = DxHandler.Device->GetDeviceRemovedReason();
				if (deviceHr.FAILED)
				{
					Console.Error.WriteLine($"ReadbackTimer: D3D device removed AFTER CopyResource (reason={deviceHr}). Game background texture may be stale.");
					_deviceLost = true;
					_hasGameBackground = false;
					StopReadbackTimer();
					context->Release();
					return;
				}

				// Map staging texture for CPU read
				D3D11_MAPPED_SUBRESOURCE mapped;
				HRESULT hr = context->Map((ID3D11Resource*)_stagingTexture, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
				if (hr.SUCCEEDED && mapped.pData != null && mapped.RowPitch > 0)
				{
					int w = _stagingWidth;
					int h = _stagingHeight;
					int srcRowPitch = (int)mapped.RowPitch;
					int dstRowPitch = w * 4;
					int imageSize = dstRowPitch * h;
					int fileSize = 54 + imageSize;
					bool needSwapRB = _stagingFormat == DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;

					// Get reusable BMP buffer and always write header
					byte[] bmp = _frameBuffer.GetReusableBuffer(fileSize);
					WriteBmpHeader(bmp, w, h, fileSize, imageSize);

					// Copy GPU mapped memory directly into BMP pixel area (offset 54)
					byte* srcBase = (byte*)mapped.pData;
					if (srcRowPitch == dstRowPitch)
					{
						// Row pitch matches — single bulk copy
						Marshal.Copy((IntPtr)srcBase, bmp, 54, imageSize);
					}
					else
					{
						// Row pitch differs — copy row by row
						for (int y = 0; y < h; y++)
						{
							Marshal.Copy((IntPtr)(srcBase + y * srcRowPitch), bmp, 54 + y * dstRowPitch, dstRowPitch);
						}
					}

					context->Unmap((ID3D11Resource*)_stagingTexture, 0);

					// In-place R/B swap if needed (rare — game usually uses B8G8R8A8)
					if (needSwapRB)
					{
						for (int i = 54; i < 54 + imageSize; i += 4)
						{
							(bmp[i], bmp[i + 2]) = (bmp[i + 2], bmp[i]);
						}
					}

					_frameBuffer.UpdateFrame(bmp, fileSize);
					int successNum = Interlocked.Increment(ref _readbackSuccessCount);
					if (successNum <= 3 || successNum % 300 == 0)
						Console.WriteLine($"ReadbackTimer[buf={_frameBufferId}] frame produced: {w}x{h} size={fileSize} [#{successNum}]");
				}
				else if (hr.SUCCEEDED)
				{
					// Map succeeded but data is invalid
					Console.Error.WriteLine("ReadbackTimer: Map succeeded but pData is null or RowPitch is 0");
					context->Unmap((ID3D11Resource*)_stagingTexture, 0);
				}
				else
				{
					Console.Error.WriteLine($"ReadbackTimer: Map failed with hr={hr}");
				}

				context->Release();
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"ReadbackTimerCallback error: {ex.Message}");
			_hasGameBackground = false;
			_cachedGameBgHandle = IntPtr.Zero;
			StopReadbackTimer();
		}
	}

	private static void WriteBmpHeader(byte[] bmp, int width, int height, int fileSize, int imageSize)
	{
		// BMP file header (14 bytes)
		bmp[0] = (byte)'B';
		bmp[1] = (byte)'M';
		bmp[2] = (byte)(fileSize);
		bmp[3] = (byte)(fileSize >> 8);
		bmp[4] = (byte)(fileSize >> 16);
		bmp[5] = (byte)(fileSize >> 24);
		bmp[6] = 0; bmp[7] = 0; bmp[8] = 0; bmp[9] = 0; // reserved
		bmp[10] = 54; bmp[11] = 0; bmp[12] = 0; bmp[13] = 0; // pixel data offset

		// DIB header (BITMAPINFOHEADER, 40 bytes)
		bmp[14] = 40; bmp[15] = 0; bmp[16] = 0; bmp[17] = 0; // header size
		bmp[18] = (byte)(width);
		bmp[19] = (byte)(width >> 8);
		bmp[20] = (byte)(width >> 16);
		bmp[21] = (byte)(width >> 24);
		int negHeight = -height;
		bmp[22] = (byte)(negHeight);
		bmp[23] = (byte)(negHeight >> 8);
		bmp[24] = (byte)(negHeight >> 16);
		bmp[25] = (byte)(negHeight >> 24);
		bmp[26] = 1; bmp[27] = 0; // planes
		bmp[28] = 32; bmp[29] = 0; // bpp
		bmp[30] = 0; bmp[31] = 0; bmp[32] = 0; bmp[33] = 0; // compression
		bmp[34] = (byte)(imageSize);
		bmp[35] = (byte)(imageSize >> 8);
		bmp[36] = (byte)(imageSize >> 16);
		bmp[37] = (byte)(imageSize >> 24);
		for (int i = 38; i < 54; i++) bmp[i] = 0;
	}

	private void ReleaseStagingTexture()
	{
		if (_stagingTexture != null)
		{
			_stagingTexture->Release();
			_stagingTexture = null;
		}
		_stagingWidth = 0;
		_stagingHeight = 0;
	}

	private void ReleaseGameBackgroundResources()
	{
		StopReadbackTimer();
		ReleaseStagingTexture();
		if (_gameBackgroundTexture != null)
		{
			_gameBackgroundTexture->Release();
			_gameBackgroundTexture = null;
		}
		_hasGameBackground = false;
		_frameBuffer.Clear();
	}

	public void Resize(Size size)
	{
		lock (_d3dLock)
		{
			// TODO: make this thread unsafe crap thread safe crap
			ID3D11Texture2D* oldTexture1 = _sharedTexture;
			ID3D11Texture2D* oldTexture2 = _viewTexture;
			_sharedTexture = BuildViewTexture(size, true);
			_viewTexture = BuildViewTexture(size, false);
			_obsoleteTextures.Add((nint)oldTexture1);
			_obsoleteTextures.Add((nint)oldTexture2);

			// Need to clear the cached handle value
			// TODO: Maybe I should just avoid the lazy cache and do it eagerly on _sharedTexture build.
			_sharedTextureHandle = IntPtr.Zero;
		}
	}

	protected byte GetAlphaAt(int x, int y)
	{
		lock (_d3dLock)
		{
			int rowPitch = _alphaLookupBufferWidth * _bytesPerPixel;

			// Get the offset for the alpha of the cursor's current position. Bitmap buffer is BGRA, so +3 to get alpha byte
			int cursorAlphaOffset = 0
			                        + (Math.Min(Math.Max(x, 0), _alphaLookupBufferWidth - 1) * _bytesPerPixel)
			                        + (Math.Min(Math.Max(y, 0), _alphaLookupBufferHeight - 1) * rowPitch)
			                        + 3;
			cursorAlphaOffset = cursorAlphaOffset < 0 ? 0 : cursorAlphaOffset;

			if (cursorAlphaOffset < _alphaLookupBuffer.Length)
			{
				return _alphaLookupBuffer[cursorAlphaOffset];
			}

			Console.WriteLine("Could not determine alpha value");
			return 255;
		}
	}

	private ID3D11Texture2D* BuildViewTexture(Size size, bool isShared)
	{
		// Build _sharedTexture. Most of these properties are defined to match how CEF exposes the render buffer.
		D3D11_TEXTURE2D_DESC desc = new()
		{
			Width = (uint)size.Width,
			Height = (uint)size.Height,
			MipLevels = 1,
			ArraySize = 1,
			Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
			SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
			Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
			BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
			CPUAccessFlags = 0,
			MiscFlags = isShared ? (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED : 0
		};

		ID3D11Texture2D* texture;
		HRESULT hr = DxHandler.Device->CreateTexture2D(&desc, null, &texture);
		if (hr.FAILED)
		{
			Console.Error.WriteLine($"BuildViewTexture failed: {hr} (size={size.Width}x{size.Height}, shared={isShared})");
			throw new Exception($"Failed to create texture: {hr}");
		}

		Console.WriteLine($"BuildViewTexture: {size.Width}x{size.Height} shared={isShared} ok");
		return texture;
	}

	private Rect GetViewRectInternal()
	{
		D3D11_TEXTURE2D_DESC texDesc;
		_sharedTexture->GetDesc(&texDesc);
		return DpiScaling.ScaleViewRect(new Rect(0, 0, (int)texDesc.Width, (int)texDesc.Height));
	}

	public void SetMousePosition(int x, int y)
	{
		byte alpha = GetAlphaAt(x, y);

		// We treat 0 alpha as click through - if changed, fire off the event
		bool currentlyOnBackground = alpha == 0;
		if (currentlyOnBackground != _cursorOnBackground)
		{
			_cursorOnBackground = currentlyOnBackground;

			// EDGE CASE: if cursor transitions onto alpha:0 _and_ between two native cursor types, I guess this will be a race cond.
			// Not sure if should have two separate upstreams for them, or try and prevent the race. consider.
			CursorChanged?.Invoke(this, currentlyOnBackground ? Cursor.BrowsingwayNoCapture : _cursor);
		}
	}

	private Cursor EncodeCursor(CursorType cursor)
	{
		switch (cursor)
		{
			// CEF calls default "pointer", and pointer "hand".
			case CursorType.Pointer: return Cursor.Default;
			case CursorType.Cross: return Cursor.Crosshair;
			case CursorType.Hand: return Cursor.Pointer;
			case CursorType.IBeam: return Cursor.Text;
			case CursorType.Wait: return Cursor.Wait;
			case CursorType.Help: return Cursor.Help;
			case CursorType.EastResize: return Cursor.EResize;
			case CursorType.NorthResize: return Cursor.NResize;
			case CursorType.NortheastResize: return Cursor.NeResize;
			case CursorType.NorthwestResize: return Cursor.NwResize;
			case CursorType.SouthResize: return Cursor.SResize;
			case CursorType.SoutheastResize: return Cursor.SeResize;
			case CursorType.SouthwestResize: return Cursor.SwResize;
			case CursorType.WestResize: return Cursor.WResize;
			case CursorType.NorthSouthResize: return Cursor.NsResize;
			case CursorType.EastWestResize: return Cursor.EwResize;
			case CursorType.NortheastSouthwestResize: return Cursor.NeswResize;
			case CursorType.NorthwestSoutheastResize: return Cursor.NwseResize;
			case CursorType.ColumnResize: return Cursor.ColResize;
			case CursorType.RowResize: return Cursor.RowResize;

			// There isn't really support for panning right now. Default to all-scroll.
			case CursorType.MiddlePanning:
			case CursorType.EastPanning:
			case CursorType.NorthPanning:
			case CursorType.NortheastPanning:
			case CursorType.NorthwestPanning:
			case CursorType.SouthPanning:
			case CursorType.SoutheastPanning:
			case CursorType.SouthwestPanning:
			case CursorType.WestPanning:
				return Cursor.AllScroll;

			case CursorType.Move: return Cursor.Move;
			case CursorType.VerticalText: return Cursor.VerticalText;
			case CursorType.Cell: return Cursor.Cell;
			case CursorType.ContextMenu: return Cursor.ContextMenu;
			case CursorType.Alias: return Cursor.Alias;
			case CursorType.Progress: return Cursor.Progress;
			case CursorType.NoDrop: return Cursor.NoDrop;
			case CursorType.Copy: return Cursor.Copy;
			case CursorType.None: return Cursor.None;
			case CursorType.NotAllowed: return Cursor.NotAllowed;
			case CursorType.ZoomIn: return Cursor.ZoomIn;
			case CursorType.ZoomOut: return Cursor.ZoomOut;
			case CursorType.Grab: return Cursor.Grab;
			case CursorType.Grabbing: return Cursor.Grabbing;

			// Not handling custom for now
			case CursorType.Custom: return Cursor.Default;
		}

		// Unmapped cursor, log and default
		Console.WriteLine($"Switching to unmapped cursor type {cursor}.");
		return Cursor.Default;
	}
}