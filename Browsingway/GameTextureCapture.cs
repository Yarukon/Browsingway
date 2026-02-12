using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Browsingway;

internal unsafe class GameTextureCapture : IDisposable
{
	private const int GameRenderTargetWithUiIndex = 106;

	private ID3D11Texture2D* _sharedTexture;
	private IntPtr _sharedHandle;
	private int _sharedWidth;
	private int _sharedHeight;

	public IntPtr SharedHandle => _sharedHandle;
	public int Width => _sharedWidth;
	public int Height => _sharedHeight;

	/// <summary>
	/// Captures the game framebuffer region matching the overlay's screen position
	/// and copies it into a shared texture for IPC to the renderer process.
	/// </summary>
	public bool CaptureForOverlay(int overlayX, int overlayY, int overlayW, int overlayH)
	{
		if (overlayW <= 0 || overlayH <= 0)
			return false;

		ID3D11Device* device = DxHandler.Device;
		if (device == null)
			return false;

		ID3D11Texture2D* gameTex = TryGetGameRenderTargetTexture();
		if (gameTex == null)
			return false;

		D3D11_TEXTURE2D_DESC gameDesc;
		gameTex->GetDesc(&gameDesc);

		// Clamp the source box to the game texture bounds
		uint srcLeft = (uint)Math.Max(overlayX, 0);
		uint srcTop = (uint)Math.Max(overlayY, 0);
		uint srcRight = (uint)Math.Min(overlayX + overlayW, (int)gameDesc.Width);
		uint srcBottom = (uint)Math.Min(overlayY + overlayH, (int)gameDesc.Height);

		if (srcRight <= srcLeft || srcBottom <= srcTop)
			return false;

		int cropW = (int)(srcRight - srcLeft);
		int cropH = (int)(srcBottom - srcTop);

		// Recreate shared texture if size changed
		if (_sharedTexture == null || _sharedWidth != cropW || _sharedHeight != cropH)
		{
			ReleaseSharedTexture();
			_sharedTexture = CreateSharedTexture(device, cropW, cropH, gameDesc.Format);
			if (_sharedTexture == null)
				return false;

			_sharedWidth = cropW;
			_sharedHeight = cropH;
			_sharedHandle = GetSharedHandle(_sharedTexture);
		}

		// Copy the cropped region from game framebuffer to shared texture
		D3D11_BOX srcBox = new()
		{
			left = srcLeft,
			top = srcTop,
			right = srcRight,
			bottom = srcBottom,
			front = 0,
			back = 1
		};

		ID3D11DeviceContext* context;
		device->GetImmediateContext(&context);
		context->CopySubresourceRegion(
			(ID3D11Resource*)_sharedTexture, 0, 0, 0, 0,
			(ID3D11Resource*)gameTex, 0, &srcBox);
		context->Flush();
		context->Release();

		return true;
	}

	private static ID3D11Texture2D* TryGetGameRenderTargetTexture()
	{
		RenderTargetManager* rtManager = RenderTargetManager.Instance();
		if (rtManager == null)
			return null;

		try
		{
			ulong rtManagerAddr = (ulong)rtManager + 0x20;
			Texture* texture = *(Texture**)(rtManagerAddr + (ulong)(0x8 * GameRenderTargetWithUiIndex));
			if (texture == null || texture->D3D11Texture2D == null)
				return null;

			return (ID3D11Texture2D*)texture->D3D11Texture2D;
		}
		catch
		{
			return null;
		}
	}

	private static ID3D11Texture2D* CreateSharedTexture(ID3D11Device* device, int width, int height, DXGI_FORMAT format)
	{
		D3D11_TEXTURE2D_DESC desc = new()
		{
			Width = (uint)width,
			Height = (uint)height,
			MipLevels = 1,
			ArraySize = 1,
			Format = format,
			SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
			Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
			BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
			CPUAccessFlags = 0,
			MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED
		};

		ID3D11Texture2D* texture;
		HRESULT hr = device->CreateTexture2D(&desc, null, &texture);
		return hr.FAILED ? null : texture;
	}

	private static IntPtr GetSharedHandle(ID3D11Texture2D* texture)
	{
		IDXGIResource* resource;
		Guid resourceGuid = typeof(IDXGIResource).GUID;
		HRESULT hr = ((IUnknown*)texture)->QueryInterface(&resourceGuid, (void**)&resource);
		if (hr.FAILED)
			return IntPtr.Zero;

		HANDLE sharedHandle;
		resource->GetSharedHandle(&sharedHandle);
		resource->Release();
		return (IntPtr)sharedHandle.Value;
	}

	private void ReleaseSharedTexture()
	{
		if (_sharedTexture != null)
		{
			_sharedTexture->Release();
			_sharedTexture = null;
		}
		_sharedHandle = IntPtr.Zero;
		_sharedWidth = 0;
		_sharedHeight = 0;
	}

	public void Dispose()
	{
		ReleaseSharedTexture();
	}
}