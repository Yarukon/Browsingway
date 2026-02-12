using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace CleanCapturePlugin
{
    /// <summary>
    /// 使用 TerraFX 直接在 FF14 的 Present 之前复制一帧干净的游戏画面，
    /// 并通过 SRV 句柄提供给 ImGui / OBS 预览使用。
    /// </summary>
    public unsafe class CleanCapture : IDisposable
    {
        private readonly IGameInteropProvider _interopProvider;
        private readonly IPluginLog _log;
        private readonly IDalamudPluginInterface _inter;
        private delegate int PresentDelegate(IntPtr swapChainPtr, int syncInterval, int flags);
        private Hook<PresentDelegate>? _presentHook;

        private readonly RenderTargetManager* _renderTargetManager;
        private volatile bool _captureIncludeGameUi = false;
        private int _gameWindowWithUiIndex = 106;
        private int _gameWindowWithoutUiIndex = 71;

        private ID3D11Device* _device;
        private ID3D11DeviceContext* _context;
        private ID3D11Texture2D* _captureTexture;
        private ID3D11ShaderResourceView* _captureSrv;
        private ID3D11Texture2D* _outputTexture;
        private ID3D11ShaderResourceView* _outputSrv;
        private ID3D11UnorderedAccessView* _outputUav;
        private ID3D11ComputeShader* _alphaFixShader;
        private ID3D11Texture2D* _spoutReadbackTexture;
        private byte[]? _spoutCpuBuffer;
        private uint _spoutBufferWidth;
        private uint _spoutBufferHeight;

        private int _width;
        private int _height;
        private DXGI_FORMAT _lastFormat = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN;
        private volatile bool _captureRequested;
        private bool _externalWindowRequested;
        private ExternalPreviewWindow? _externalPreviewWindow;
        private bool _spoutRequested;
        private SpoutOutput? _spoutOutput;
        private int _presentSuppression;

        public CleanCapture(IDalamudPluginInterface pluginInterface, IGameInteropProvider interopProvider, IPluginLog log)
        {
            _ = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
            _inter = pluginInterface;
            _interopProvider = interopProvider;
            _log = log;
            _renderTargetManager = RenderTargetManager.Instance();
            InitializeHook();
        }

        public bool CaptureRequested
        {
            get => _captureRequested;
            set => _captureRequested = value;
        }

        /// <summary>
        /// 捕获画面是否包含游戏 UI（通过 FFXIV RenderTargetManager 的不同缓冲区切换）。
        /// </summary>
        public bool CaptureIncludeGameUi
        {
            get => _captureIncludeGameUi;
            set => _captureIncludeGameUi = value;
        }

        public int GameWindowWithUiIndex
        {
            get => _gameWindowWithUiIndex;
            set => _gameWindowWithUiIndex = value;
        }

        public int GameWindowWithoutUiIndex
        {
            get => _gameWindowWithoutUiIndex;
            set => _gameWindowWithoutUiIndex = value;
        }

        public IntPtr GetTextureHandle() => _outputSrv != null ? (IntPtr)_outputSrv : IntPtr.Zero;

        public (int width, int height) GetTextureSize() => (_width, _height);

        public void SetExternalPreviewEnabled(bool enabled)
        {
            _externalWindowRequested = enabled;
            if (!enabled)
            {
                _externalPreviewWindow?.Dispose();
                _externalPreviewWindow = null;
            }
            else
            {
                TryInitializeExternalWindow();
            }
        }

        public void SetSpoutOutputEnabled(bool enabled)
        {
            _spoutRequested = enabled;
            if (!enabled)
            {
                _spoutOutput?.Dispose();
                _spoutOutput = null;
                ReleaseSpoutResources();
            }
            else
            {
                TryInitializeSpoutOutput();
            }
        }

        private void InitializeHook()
        {
            DXGI_SWAP_CHAIN_DESC desc = default;
            desc.BufferDesc.Width = 32;
            desc.BufferDesc.Height = 32;
            desc.BufferDesc.Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
            desc.BufferDesc.RefreshRate.Numerator = 60;
            desc.BufferDesc.RefreshRate.Denominator = 1;
            desc.SampleDesc.Count = 1;
            desc.SampleDesc.Quality = 0;
            desc.BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT;
            desc.BufferCount = 1;
            desc.OutputWindow = (HWND)Process.GetCurrentProcess().MainWindowHandle;
            desc.Windowed = BOOL.TRUE;
            desc.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_DISCARD;

            IDXGISwapChain* dummySwapChain = null;
            ID3D11Device* dummyDevice = null;
            ID3D11DeviceContext* dummyContext = null;
            var featureLevel = D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0;
            var hr = DirectX.D3D11CreateDeviceAndSwapChain(
                null,
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                default,
                (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                &featureLevel,
                1,
                D3D11.D3D11_SDK_VERSION,
                &desc,
                &dummySwapChain,
                &dummyDevice,
                null,
                &dummyContext);

            if (hr < 0 || dummySwapChain == null)
            {
                _log.Error("[CleanCapture] 无法创建 Dummy SwapChain，无法挂钩 Present。");
                if (dummyContext != null)
                {
                    dummyContext->Release();
                }

                if (dummyDevice != null)
                {
                    dummyDevice->Release();
                }
                return;
            }

            var vtable = *(void***)dummySwapChain;
            var presentPtr = (IntPtr)vtable[8];
            dummyContext->Release();
            dummySwapChain->Release();
            dummyDevice->Release();

            _presentHook = _interopProvider.HookFromAddress<PresentDelegate>(presentPtr, PresentDetour);
            _presentHook.Enable();
            _log.Information($"[CleanCapture] Present hook 初始化完成 @0x{presentPtr.ToInt64():X}");
        }

        private int PresentDetour(IntPtr swapChainPtr, int syncInterval, int flags)
        {
            if (_captureRequested && Volatile.Read(ref _presentSuppression) == 0)
            {
                try
                {
                    CaptureFrame((IDXGISwapChain*)swapChainPtr);
                }
                catch (Exception ex)
                {
                    _log.Verbose($"[CleanCapture] 捕获失败: {ex.Message}");
                }
            }

            return _presentHook!.Original(swapChainPtr, syncInterval, flags);
        }

        private void CaptureFrame(IDXGISwapChain* swapChain)
        {
            if (swapChain == null)
            {
                return;
            }

            swapChain->AddRef();
            try
            {
                EnsureDevice(swapChain);
                if (_device == null || _context == null)
                {
                    return;
                }

                ID3D11Texture2D* sourceTexture = null;
                var sourceNeedsRelease = false;
                var sourceDesc = new D3D11_TEXTURE2D_DESC();
                try
                {
                    sourceTexture = TryGetGameRenderTargetTexture();
                    if (sourceTexture != null)
                    {
                        ((IUnknown*)sourceTexture)->AddRef();
                        sourceNeedsRelease = true;
                    }
                    else
                    {
                        var texGuid = IID.IID_ID3D11Texture2D;
                        var hr = swapChain->GetBuffer(0, &texGuid, (void**)&sourceTexture);
                        if (hr < 0 || sourceTexture == null)
                        {
                            return;
                        }
                        sourceNeedsRelease = true;
                    }

                    sourceTexture->GetDesc(&sourceDesc);
                    EnsureTextures(in sourceDesc);
                    if (_captureTexture == null || _outputTexture == null)
                    {
                        return;
                    }

                    _context->CopyResource((ID3D11Resource*)_captureTexture, (ID3D11Resource*)sourceTexture);
                }
                finally
                {
                    if (sourceNeedsRelease && sourceTexture != null)
                    {
                        ((IUnknown*)sourceTexture)->Release();
                    }
                }

                FixAlphaChannel((int)sourceDesc.Width, (int)sourceDesc.Height);
                if (_externalPreviewWindow != null)
                {
                    var width = (uint)_width;
                    var height = (uint)_height;
                    RunWithPresentSuppressed(() => _externalPreviewWindow.Present(_outputTexture, _context, width, height));
                }

                TrySendSpoutFrame((uint)_width, (uint)_height);
            }
            finally
            {
                swapChain->Release();
            }
        }

        private ID3D11Texture2D* TryGetGameRenderTargetTexture()
        {
            if (_renderTargetManager == null)
            {
                return null;
            }

            var index = _captureIncludeGameUi ? _gameWindowWithUiIndex : _gameWindowWithoutUiIndex;
            index = Math.Min(Math.Max(index, 0), 129);

            try
            {
                var rtManagerAddr = ((ulong)_renderTargetManager) + 0x20;
                var texture = *(Texture**)(rtManagerAddr + (ulong)(0x8 * index));
                if (texture == null || texture->D3D11Texture2D == null)
                {
                    return null;
                }

                return (ID3D11Texture2D*)texture->D3D11Texture2D;
            }
            catch
            {
                return null;
            }
        }

        private void EnsureDevice(IDXGISwapChain* swapChain)
        {
            ID3D11Device* device = null;
            var deviceGuid = IID.IID_ID3D11Device;
            var hr = swapChain->GetDevice(&deviceGuid, (void**)&device);
            if (hr < 0 || device == null)
            {
                return;
            }

            if (_device == null)
            {
                _device = device;
                ID3D11DeviceContext* ctx = null;
                _device->GetImmediateContext(&ctx);
                _context = ctx;
                CreateAlphaFixShader();
                _log.Information("[CleanCapture] 已获取 D3D11 设备引用。");
            }
            else if (_device != device)
            {
                ReleaseTextures();
                ReleaseCom(ref _context);
                ReleaseCom(ref _device);
                _device = device;
                ID3D11DeviceContext* ctx = null;
                _device->GetImmediateContext(&ctx);
                _context = ctx;
                CreateAlphaFixShader();
                _log.Information("[CleanCapture] 侦测到设备更换，已重新初始化。");
            }
            else
            {
                device->Release();
            }
        }

        private void EnsureTextures(in D3D11_TEXTURE2D_DESC backBufferDesc)
        {
            if (_device == null)
            {
                return;
            }

            if (_captureTexture != null &&
                _width == backBufferDesc.Width &&
                _height == backBufferDesc.Height &&
                _lastFormat == backBufferDesc.Format)
            {
                return;
            }

            ReleaseTextures();

            var captureDesc = stackalloc D3D11_TEXTURE2D_DESC[1];
            *captureDesc = backBufferDesc;
            captureDesc->BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
            captureDesc->CPUAccessFlags = 0;
            captureDesc->MiscFlags = 0;
            captureDesc->Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;

            ID3D11Texture2D* captureTexture = null;
            ThrowIfFailed(_device->CreateTexture2D(captureDesc, null, &captureTexture));
            _captureTexture = captureTexture;

            var captureSrvDesc = stackalloc D3D11_SHADER_RESOURCE_VIEW_DESC[1];
            captureSrvDesc->Format = captureDesc->Format;
            captureSrvDesc->ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D;
            captureSrvDesc->Anonymous.Texture2D.MipLevels = 1;
            captureSrvDesc->Anonymous.Texture2D.MostDetailedMip = 0;
            ID3D11ShaderResourceView* captureSrv = null;
            ThrowIfFailed(_device->CreateShaderResourceView((ID3D11Resource*)_captureTexture, captureSrvDesc, &captureSrv));
            _captureSrv = captureSrv;

            var outputDesc = stackalloc D3D11_TEXTURE2D_DESC[1];
            outputDesc->Width = backBufferDesc.Width;
            outputDesc->Height = backBufferDesc.Height;
            outputDesc->MipLevels = 1;
            outputDesc->ArraySize = 1;
            outputDesc->Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
            outputDesc->SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 };
            outputDesc->Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
            outputDesc->BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS);
            outputDesc->CPUAccessFlags = 0;
            outputDesc->MiscFlags = 0;

            ID3D11Texture2D* outputTexture = null;
            ThrowIfFailed(_device->CreateTexture2D(outputDesc, null, &outputTexture));
            _outputTexture = outputTexture;

            var outputSrvDesc = stackalloc D3D11_SHADER_RESOURCE_VIEW_DESC[1];
            outputSrvDesc->Format = outputDesc->Format;
            outputSrvDesc->ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D;
            outputSrvDesc->Anonymous.Texture2D.MipLevels = 1;
            outputSrvDesc->Anonymous.Texture2D.MostDetailedMip = 0;
            ID3D11ShaderResourceView* outputSrv = null;
            ThrowIfFailed(_device->CreateShaderResourceView((ID3D11Resource*)_outputTexture, outputSrvDesc, &outputSrv));
            _outputSrv = outputSrv;

            var uavDesc = stackalloc D3D11_UNORDERED_ACCESS_VIEW_DESC[1];
            uavDesc->Format = outputDesc->Format;
            uavDesc->ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_TEXTURE2D;
            ID3D11UnorderedAccessView* outputUav = null;
            ThrowIfFailed(_device->CreateUnorderedAccessView((ID3D11Resource*)_outputTexture, uavDesc, &outputUav));
            _outputUav = outputUav;

            _width = (int)backBufferDesc.Width;
            _height = (int)backBufferDesc.Height;
            _lastFormat = backBufferDesc.Format;
            _log.Information($"[CleanCapture] 画面尺寸更新: {_width}x{_height}");

            TryInitializeExternalWindow();
            TryInitializeSpoutOutput();
        }

        private void TryInitializeExternalWindow()
        {
            if (!_externalWindowRequested || _externalPreviewWindow != null || _device == null)
            {
                return;
            }

            var preview = new ExternalPreviewWindow(_log);
            if (preview.TryInitialize(_device))
            {
                _externalPreviewWindow = preview;
            }
            else
            {
                preview.Dispose();
                _log.Warning("[CleanCapture] 系统预览窗口初始化失败。");
            }
        }

        private void TryInitializeSpoutOutput()
        {
            if (!_spoutRequested)
            {
                return;
            }

            _spoutOutput ??= new SpoutOutput(_log, _inter);
        }

        private void RunWithPresentSuppressed(Action action)
        {
            Interlocked.Increment(ref _presentSuppression);
            try
            {
                action();
            }
            finally
            {
                Interlocked.Decrement(ref _presentSuppression);
            }
        }

        private void FixAlphaChannel(int width, int height)
        {
            if (_alphaFixShader == null || _captureSrv == null || _outputUav == null)
            {
                return;
            }

            var srv = _captureSrv;
            _context->CSSetShader(_alphaFixShader, null, 0);
            _context->CSSetShaderResources(0, 1, &srv);

            var uav = _outputUav;
            uint append = 0;
            _context->CSSetUnorderedAccessViews(0, 1, &uav, &append);

            uint dispatchX = (uint)Math.Max(1, (width + 7) / 8);
            uint dispatchY = (uint)Math.Max(1, (height + 7) / 8);
            _context->Dispatch(dispatchX, dispatchY, 1);

            ID3D11ShaderResourceView* nullSrv = null;
            _context->CSSetShaderResources(0, 1, &nullSrv);
            ID3D11UnorderedAccessView* nullUav = null;
            uint zero = 0;
            _context->CSSetUnorderedAccessViews(0, 1, &nullUav, &zero);
            _context->CSSetShader(null, null, 0);
        }

        private void TrySendSpoutFrame(uint width, uint height)
        {
            if (!_spoutRequested || _spoutOutput == null || _context == null || _outputTexture == null)
            {
                return;
            }

            if (!EnsureSpoutReadback(width, height))
            {
                return;
            }

            if (!CopyOutputToCpuBuffer(width, height))
            {
                return;
            }

            if (_spoutCpuBuffer != null)
            {
                _spoutOutput.SendCpuFrame(_spoutCpuBuffer, width, height);
            }
        }

        private unsafe bool EnsureSpoutReadback(uint width, uint height)
        {
            if (_device == null)
            {
                return false;
            }

            if (_spoutReadbackTexture != null && _spoutBufferWidth == width && _spoutBufferHeight == height && _spoutCpuBuffer != null)
            {
                return true;
            }

            ReleaseSpoutResources();

            var desc = stackalloc D3D11_TEXTURE2D_DESC[1];
            desc->Width = width;
            desc->Height = height;
            desc->MipLevels = 1;
            desc->ArraySize = 1;
            desc->Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
            desc->SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 };
            desc->Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
            desc->BindFlags = 0;
            desc->CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
            desc->MiscFlags = 0;

            ID3D11Texture2D* staging = null;
            if (_device->CreateTexture2D(desc, null, &staging) < 0 || staging == null)
            {
                return false;
            }

            _spoutReadbackTexture = staging;
            try
            {
                checked
                {
                    _spoutCpuBuffer = new byte[width * height * 4];
                }
            }
            catch
            {
                ReleaseSpoutResources();
                return false;
            }

            _spoutBufferWidth = width;
            _spoutBufferHeight = height;
            return true;
        }

        private unsafe bool CopyOutputToCpuBuffer(uint width, uint height)
        {
            if (_spoutReadbackTexture == null || _context == null || _outputTexture == null || _spoutCpuBuffer == null)
            {
                return false;
            }

            _context->CopyResource((ID3D11Resource*)_spoutReadbackTexture, (ID3D11Resource*)_outputTexture);

            var mapped = new D3D11_MAPPED_SUBRESOURCE();
            var hr = _context->Map((ID3D11Resource*)_spoutReadbackTexture, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
            if (hr < 0)
            {
                return false;
            }

            try
            {
                var rowBytes = (int)width * 4;
                var totalBytes = rowBytes * (int)height;

                fixed (byte* destPtr = _spoutCpuBuffer)
                {
                    if (mapped.RowPitch == (uint)rowBytes)
                    {
                        var srcSpan = new Span<byte>((byte*)mapped.pData, totalBytes);
                        var dstSpan = new Span<byte>(destPtr, totalBytes);
                        srcSpan.CopyTo(dstSpan);
                    }
                    else
                    {
                        var dstSpan = new Span<byte>(destPtr, totalBytes);
                        for (uint y = 0; y < height; y++)
                        {
                            var srcSpan = new Span<byte>((byte*)mapped.pData + y * mapped.RowPitch, rowBytes);
                            srcSpan.CopyTo(dstSpan.Slice((int)(y * (uint)rowBytes), rowBytes));
                        }
                    }
                }

                return true;
            }
            finally
            {
                _context->Unmap((ID3D11Resource*)_spoutReadbackTexture, 0);
            }
        }

        private void ReleaseSpoutResources()
        {
            ReleaseCom(ref _spoutReadbackTexture);
            _spoutCpuBuffer = null;
            _spoutBufferWidth = 0;
            _spoutBufferHeight = 0;
        }

        private void CreateAlphaFixShader()
        {
            ReleaseCom(ref _alphaFixShader);
            if (_device == null)
            {
                return;
            }

            const string shaderCode = @"
Texture2D<float4> InputTexture : register(t0);
RWTexture2D<float4> OutputTexture : register(u0);
[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    float4 color = InputTexture[dispatchThreadID.xy];
    color.a = 1.0f;
    OutputTexture[dispatchThreadID.xy] = color;
}";

            var sourceBytes = Encoding.UTF8.GetBytes(shaderCode);
            fixed (byte* shaderPtr = sourceBytes)
            {
                byte* entryPtr = stackalloc byte[16];
                var entrySpan = new Span<byte>(entryPtr, 16);
                var entryLen = Encoding.UTF8.GetBytes("CSMain", entrySpan);
                entrySpan[entryLen] = 0;

                byte* targetPtr = stackalloc byte[16];
                var targetSpan = new Span<byte>(targetPtr, 16);
                var targetLen = Encoding.UTF8.GetBytes("cs_5_0", targetSpan);
                targetSpan[targetLen] = 0;

                ID3DBlob* shaderBlob = null;
                ID3DBlob* errorBlob = null;
                var hr = D3DCompile(
                    shaderPtr,
                    (nuint)sourceBytes.Length,
                    null,
                    null,
                    null,
                    (sbyte*)entryPtr,
                    (sbyte*)targetPtr,
                    0,
                    0,
                    &shaderBlob,
                    &errorBlob);

                if (hr < 0 || shaderBlob == null)
                {
                    string message = "unknown";
                    if (errorBlob != null)
                    {
                        message = Marshal.PtrToStringAnsi((IntPtr)errorBlob->GetBufferPointer(), (int)errorBlob->GetBufferSize()) ?? "unknown";
                    }

                    _log.Error($"[CleanCapture] Alpha Shader 编译失败: {message}");
                    ReleaseBlob(ref errorBlob);
                    ReleaseBlob(ref shaderBlob);
                    return;
                }

                ID3D11ComputeShader* shader = null;
                ThrowIfFailed(_device->CreateComputeShader(shaderBlob->GetBufferPointer(), shaderBlob->GetBufferSize(), null, &shader));
                ReleaseBlob(ref shaderBlob);
                ReleaseBlob(ref errorBlob);
                _alphaFixShader = shader;
            }
        }

        private static void ThrowIfFailed(int hr)
        {
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DCompile(
            void* pSrcData,
            nuint SrcDataSize,
            sbyte* pSourceName,
            void* pDefines,
            ID3DInclude* pInclude,
            sbyte* pEntryPoint,
            sbyte* pTarget,
            uint Flags1,
            uint Flags2,
            ID3DBlob** ppCode,
            ID3DBlob** ppErrorMsgs);

        private static void ReleaseBlob(ref ID3DBlob* blob)
        {
            if (blob != null)
            {
                blob->Release();
                blob = null;
            }
        }

        private static void ReleaseCom<T>(ref T* com) where T : unmanaged
        {
            if (com != null)
            {
                ((IUnknown*)com)->Release();
                com = null;
            }
        }

        private void ReleaseTextures()
        {
            ReleaseCom(ref _captureSrv);
            ReleaseCom(ref _outputSrv);
            ReleaseCom(ref _outputUav);
            ReleaseCom(ref _captureTexture);
            ReleaseCom(ref _outputTexture);
            ReleaseSpoutResources();
            _width = 0;
            _height = 0;
            _lastFormat = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN;
        }

        public void Dispose()
        {
            _captureRequested = false;
            _presentHook?.Disable();
            _presentHook?.Dispose();
            _presentHook = null;

            ReleaseTextures();
            ReleaseCom(ref _alphaFixShader);
            ReleaseCom(ref _context);
            ReleaseCom(ref _device);
            _externalPreviewWindow?.Dispose();
            _externalPreviewWindow = null;
            _spoutOutput?.Dispose();
            _spoutOutput = null;
            ReleaseSpoutResources();
        }
    }

    internal unsafe sealed class ExternalPreviewWindow : IDisposable
    {
        private const int DefaultWidth = 1280;
        private const int DefaultHeight = 720;
        private readonly IPluginLog _log;
        private Thread? _thread;
        private readonly AutoResetEvent _readyEvent = new(false);
        private bool _running;
        private HWND _hwnd;
        private ID3D11Device* _device;
        private IDXGISwapChain* _swapChain;
        private GCHandle _selfHandle;
        private char* _className;
        private char* _windowTitle;
        private bool _classRegistered;
        private readonly object _swapLock = new();
        private const int GwlpUserData = -21;
        private const uint ErrorClassAlreadyExists = 1410;

        public ExternalPreviewWindow(IPluginLog log)
        {
            _log = log;
        }

        public bool TryInitialize(ID3D11Device* device)
        {
            if (_thread != null)
                return true;
            if (device == null)
                return false;

            _device = device;
            _device->AddRef();
            _selfHandle = GCHandle.Alloc(this);
            _running = true;
            _thread = new Thread(WindowThread)
            {
                IsBackground = true,
                Name = "CleanCapturePreviewWindow"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            return _readyEvent.WaitOne(TimeSpan.FromSeconds(5));
        }

        public void Present(ID3D11Texture2D* texture, ID3D11DeviceContext* context, uint sourceWidth, uint sourceHeight)
        {
            if (texture == null || context == null)
                return;
            var swap = _swapChain;
            if (swap == null)
                return;

            EnsureBackBufferSize(sourceWidth, sourceHeight);

            lock (_swapLock)
            {
                ID3D11Texture2D* backBuffer = null;
                var texGuid = IID.IID_ID3D11Texture2D;
                if (swap->GetBuffer(0, &texGuid, (void**)&backBuffer) >= 0 && backBuffer != null)
                {
                    context->CopyResource((ID3D11Resource*)backBuffer, (ID3D11Resource*)texture);
                    backBuffer->Release();
                    swap->Present(1, 0);
                }
            }
        }

        private void EnsureBackBufferSize(uint width, uint height)
        {
            if (_swapChain == null || width == 0 || height == 0)
            {
                return;
            }

            DXGI_SWAP_CHAIN_DESC desc = default;
            _swapChain->GetDesc(&desc);
            if (desc.BufferDesc.Width == width && desc.BufferDesc.Height == height)
            {
                return;
            }

            lock (_swapLock)
            {
                _swapChain->ResizeBuffers(0, width, height, desc.BufferDesc.Format, 0);
            }

            if (_hwnd != default)
            {
                SetWindowPos(_hwnd, HWND.NULL, 0, 0, (int)width, (int)height,
                    SWP.SWP_NOMOVE | SWP.SWP_NOZORDER | SWP.SWP_NOACTIVATE);
            }
        }

        public void Dispose()
        {
            _running = false;
            if (_hwnd != default)
            {
                PostMessageW(_hwnd, WM.WM_CLOSE, 0, 0);
            }
            _thread?.Join();
            _thread = null;
            CleanupSwapChain();
            if (_hwnd != default)
            {
                DestroyWindow(_hwnd);
                _hwnd = default;
            }

            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }

            if (_device != null)
            {
                _device->Release();
                _device = null;
            }

            if (_className != null)
            {
                if (_classRegistered)
                {
                    var hInstance = GetModuleHandleW(null);
                    UnregisterClassW(_className, hInstance);
                }

                Marshal.FreeHGlobal((IntPtr)_className);
                _className = null;
                _classRegistered = false;
            }

            if (_windowTitle != null)
            {
                Marshal.FreeHGlobal((IntPtr)_windowTitle);
                _windowTitle = null;
            }
        }

        private void WindowThread()
        {
            try
            {
                var hInstance = GetModuleHandleW(null);
                _className = AllocUtf16("CleanCapturePreviewWindowClass");
                _windowTitle = AllocUtf16("FFXIV Clean Preview");

                var wc = new WNDCLASSEXW();
                wc.cbSize = (uint)sizeof(WNDCLASSEXW);
                wc.style = CS.CS_HREDRAW | CS.CS_VREDRAW;
                wc.lpfnWndProc = (delegate* unmanaged<HWND, uint, WPARAM, LPARAM, LRESULT>)(delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT>)&WndProc;
                wc.cbClsExtra = 0;
                wc.cbWndExtra = 0;
                wc.hInstance = hInstance;
                wc.hCursor = LoadCursorW(HINSTANCE.NULL, IDC.IDC_ARROW);
                wc.hbrBackground = HBRUSH.NULL;
                wc.lpszClassName = _className;

                if (RegisterClassExW(&wc) == 0)
                {
                    var err = GetLastError();
                    if (err != ErrorClassAlreadyExists)
                    {
                        _log.Error($"[CleanCapture] RegisterClassExW 失败: {err}");
                        _readyEvent.Set();
                        return;
                    }
                }
                else
                {
                    _classRegistered = true;
                }

                _hwnd = CreateWindowExW(
                    0,
                    _className,
                    _windowTitle,
                    WS.WS_OVERLAPPEDWINDOW | WS.WS_VISIBLE,
                    CW_USEDEFAULT,
                    CW_USEDEFAULT,
                    DefaultWidth,
                    DefaultHeight,
                    default,
                    default,
                    hInstance,
                    (void*)GCHandle.ToIntPtr(_selfHandle));

                if (_hwnd == default)
                {
                    _log.Error($"[CleanCapture] CreateWindowExW 失败: {GetLastError()}");
                    _readyEvent.Set();
                    return;
                }

                if (!CreateSwapChain())
                {
                    _readyEvent.Set();
                    return;
                }

                ShowWindow(_hwnd, SW.SW_SHOW);
                UpdateWindow(_hwnd);
                _readyEvent.Set();

                MSG msg;
                while (_running && GetMessageW(&msg, default, 0, 0) > 0)
                {
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[CleanCapture] 外部窗口线程异常: {ex}");
                _readyEvent.Set();
            }
        }

        private bool CreateSwapChain()
        {
            IDXGIDevice* dxgiDevice = null;
            var riid = IID.IID_IDXGIDevice;
            var hr = ((IUnknown*)_device)->QueryInterface(&riid, (void**)&dxgiDevice);
            if (hr < 0 || dxgiDevice == null)
            {
                _log.Error("[CleanCapture] 获取 IDXGIDevice 失败。");
                return false;
            }

            IDXGIAdapter* adapter = null;
            dxgiDevice->GetAdapter(&adapter);
            IDXGIFactory* factory = null;
            var factoryGuid = IID.IID_IDXGIFactory;
            adapter->GetParent(&factoryGuid, (void**)&factory);

            DXGI_SWAP_CHAIN_DESC desc = default;
            desc.BufferDesc.Width = DefaultWidth;
            desc.BufferDesc.Height = DefaultHeight;
            desc.BufferDesc.Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
            desc.BufferDesc.RefreshRate.Numerator = 60;
            desc.BufferDesc.RefreshRate.Denominator = 1;
            desc.SampleDesc.Count = 1;
            desc.SampleDesc.Quality = 0;
            desc.BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT;
            desc.BufferCount = 1;
            desc.OutputWindow = _hwnd;
            desc.Windowed = 1;
            desc.SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_DISCARD;

            IDXGISwapChain* createdSwapChain = null;
            hr = factory->CreateSwapChain((IUnknown*)_device, &desc, &createdSwapChain);
            factory->MakeWindowAssociation(_hwnd, DXGI.DXGI_MWA_NO_ALT_ENTER);

            factory->Release();
            adapter->Release();
            dxgiDevice->Release();

            if (hr < 0 || createdSwapChain == null)
            {
                _log.Error("[CleanCapture] 创建 SwapChain 失败。");
                return false;
            }

            _swapChain = createdSwapChain;
            return true;
        }

        private void CleanupSwapChain()
        {
            if (_swapChain != null)
            {
                _swapChain->Release();
                _swapChain = null;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
        {
            try
            {
                if (msg == WM.WM_CREATE)
                {
                    var create = (CREATESTRUCTW*)lParam.Value;
                    SetWindowLongPtrW(hwnd, GwlpUserData, (nint)create->lpCreateParams);
                    return DefWindowProcW(hwnd, msg, wParam, lParam);
                }

                var handle = GCHandle.FromIntPtr(GetWindowInstance(hwnd));
                if (handle.IsAllocated && handle.Target is ExternalPreviewWindow window)
                {
                    return window.HandleMessage(hwnd, msg, wParam, lParam);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CleanCapture] External preview message error: {ex}");
            }

            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        private LRESULT HandleMessage(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
        {
            switch (msg)
            {
                case WM.WM_CLOSE:
                    _running = false;
                    DestroyWindow(hwnd);
                    return 0;
                case WM.WM_DESTROY:
                    SetWindowLongPtrW(hwnd, GwlpUserData, 0);
                    PostQuitMessage(0);
                    return 0;
                case WM.WM_SIZE:
                    var width = (uint)(lParam.Value & 0xFFFF);
                    var height = (uint)((lParam.Value >> 16) & 0xFFFF);
                    HandleResize(width, height);
                    return 0;
                default:
                    return DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }

        private void HandleResize(uint width, uint height)
        {
            if (_swapChain == null || width == 0 || height == 0)
            {
                return;
            }

            lock (_swapLock)
            {
                _swapChain->ResizeBuffers(0, width, height, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 0);
            }
        }

        private static IntPtr GetWindowInstance(HWND hwnd) => (IntPtr)GetWindowLongPtrW(hwnd, GwlpUserData);

        private static char* AllocUtf16(string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value + "\0");
            var ptr = (char*)Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, (IntPtr)ptr, bytes.Length);
            return ptr;
        }
    }

    internal unsafe sealed class SpoutOutput : IDisposable
    {
        private readonly IPluginLog _log;
        private readonly IDalamudPluginInterface _interface;
        private readonly string _senderName;
        private SpoutLibraryInterop? _interop;
        private bool _ready;

        public SpoutOutput(IPluginLog log, IDalamudPluginInterface dalamudPluginInterface, string senderName = "FFXIVCleanCapture")
        {
            _interface = dalamudPluginInterface;
            _log = log;
            _senderName = senderName;
            Initialize();
        }

        private void Initialize()
        {
            var baseDir = _interface.AssemblyLocation.Directory?.FullName;
            var candidate = !string.IsNullOrEmpty(baseDir)
                ? Path.Combine(baseDir, "SpoutLibrary.dll")
                : "SpoutLibrary.dll";

            _interop = SpoutLibraryInterop.TryCreate(candidate, _senderName, _log);
            if (_interop == null && !string.Equals(candidate, "SpoutLibrary.dll", StringComparison.OrdinalIgnoreCase))
            {
                _interop = SpoutLibraryInterop.TryCreate("SpoutLibrary.dll", _senderName, _log);
            }

            _ready = _interop != null;
        }

        public void SendCpuFrame(byte[] buffer, uint width, uint height)
        {
            if (!_ready || _interop == null || buffer == null || buffer.Length == 0)
            {
                return;
            }

            _interop.SendImage(buffer, width, height);
        }

        public void Dispose()
        {
            _interop?.Dispose();
            _interop = null;
            _ready = false;
        }

        private sealed unsafe class SpoutLibraryInterop : IDisposable
        {
            private const uint GlBgraExt = 0x80E1;
            private readonly IPluginLog _log;
            private IntPtr _libraryHandle;
            private IntPtr _spoutHandle;
            private delegate* unmanaged[Stdcall]<IntPtr, byte*, void> _setSenderName;
            private delegate* unmanaged[Stdcall]<IntPtr, uint, void> _setSenderFormat;
            private delegate* unmanaged[Stdcall]<IntPtr, uint, void> _releaseSender;
            private delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint, uint, byte, bool> _sendImage;
            private delegate* unmanaged[Stdcall]<IntPtr, void> _release;
            private int _sendFailures;

            private SpoutLibraryInterop(IPluginLog log)
            {
                _log = log;
            }

            public static SpoutLibraryInterop? TryCreate(string path, string senderName, IPluginLog log)
            {
                IntPtr libraryHandle;
                try
                {
                    libraryHandle = NativeLibrary.Load(path);
                }
                catch (Exception ex)
                {
                    log.Warning($"[CleanCapture] ???? SpoutLibrary.dll ({path}): {ex.Message}");
                    return null;
                }

                if (!NativeLibrary.TryGetExport(libraryHandle, "GetSpout", out var getSpoutPtr))
                {
                    log.Warning("[CleanCapture] ?? SpoutLibrary.dll ??? GetSpout()");
                    NativeLibrary.Free(libraryHandle);
                    return null;
                }

                var getSpout = (delegate* unmanaged[Stdcall]<IntPtr>)getSpoutPtr;
                var spoutHandle = getSpout();
                if (spoutHandle == IntPtr.Zero)
                {
                    log.Warning("[CleanCapture] GetSpout ??????Spout ??????");
                    NativeLibrary.Free(libraryHandle);
                    return null;
                }

                var vtablePtr = (void***)spoutHandle;
                if (vtablePtr == null || *vtablePtr == null)
                {
                    log.Warning("[CleanCapture] ???? Spout vtable?");
                    NativeLibrary.Free(libraryHandle);
                    return null;
                }

                var vtable = *vtablePtr;
                var interop = new SpoutLibraryInterop(log)
                {
                    _libraryHandle = libraryHandle,
                    _spoutHandle = spoutHandle,
                    _setSenderName = (delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)vtable[0],
                    _setSenderFormat = (delegate* unmanaged[Stdcall]<IntPtr, uint, void>)vtable[1],
                    _releaseSender = (delegate* unmanaged[Stdcall]<IntPtr, uint, void>)vtable[2],
                    _sendImage = (delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint, uint, byte, bool>)vtable[5],
                    _release = (delegate* unmanaged[Stdcall]<IntPtr, void>)vtable[171]
                };

                interop.ConfigureSender(senderName);
                return interop;
            }

            private void ConfigureSender(string senderName)
            {
                if (_setSenderName != null)
                {
                    var nameBytes = Encoding.UTF8.GetBytes(senderName + "\0");
                    fixed (byte* namePtr = nameBytes)
                    {
                        _setSenderName(_spoutHandle, namePtr);
                    }
                }

                if (_setSenderFormat != null)
                {
                    _setSenderFormat(_spoutHandle, (uint)DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM);
                }
            }

            public void SendImage(byte[] buffer, uint width, uint height)
            {
                if (_sendImage == null || buffer == null || buffer.Length == 0)
                {
                    return;
                }

                fixed (byte* ptr = buffer)
                {
                    var success = _sendImage(_spoutHandle, ptr, width, height, GlBgraExt, 0);
                    if (!success)
                    {
                        if (_sendFailures++ < 5)
                        {
                            _log.Warning($"[CleanCapture] Spout SendImage 失败（{width}x{height}）。");
                        }
                    }
                    else
                    {
                        _sendFailures = 0;
                    }
                }
            }

            public void Dispose()
            {
                if (_spoutHandle != IntPtr.Zero)
                {
                    if (_releaseSender != null)
                    {
                        _releaseSender(_spoutHandle, 0);
                    }

                    if (_release != null)
                    {
                        _release(_spoutHandle);
                    }

                    _spoutHandle = IntPtr.Zero;
                }

                if (_libraryHandle != IntPtr.Zero)
                {
                    NativeLibrary.Free(_libraryHandle);
                    _libraryHandle = IntPtr.Zero;
                }
            }
        }
    }
}
