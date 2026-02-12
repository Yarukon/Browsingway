using CefSharp;
using System.Collections.Concurrent;

namespace Browsingway.Renderer;

/// <summary>
/// Per-overlay double-buffered frame store for game background BMP data.
/// Each overlay gets its own buffer keyed by a unique ID.
/// </summary>
internal class GameBackgroundFrameBuffer
{
	private static readonly ConcurrentDictionary<int, GameBackgroundFrameBuffer> _buffers = new();
	private static int _nextId;

	private byte[]? _frontBuffer;
	private byte[]? _backBuffer;
	private int _frontBufferValidLength;
	private readonly object _lock = new();

	public static int CreateBuffer()
	{
		int id = Interlocked.Increment(ref _nextId);
		_buffers[id] = new GameBackgroundFrameBuffer();
		Console.WriteLine($"GameBackgroundFrameBuffer.CreateBuffer: id={id}, total buffers={_buffers.Count}");
		return id;
	}

	public static void RemoveBuffer(int id)
	{
		_buffers.TryRemove(id, out _);
	}

	public static GameBackgroundFrameBuffer? Get(int id)
	{
		_buffers.TryGetValue(id, out var buf);
		return buf;
	}

	public byte[] GetReusableBuffer(int size)
	{
		lock (_lock)
		{
			if (_backBuffer == null || _backBuffer.Length < size)
				_backBuffer = new byte[size];
			return _backBuffer;
		}
	}

	public void UpdateFrame(byte[] bmpData, int validLength)
	{
		lock (_lock)
		{
			_backBuffer = _frontBuffer;
			_frontBuffer = bmpData;
			_frontBufferValidLength = validLength;
		}
	}

	public (byte[]? data, int length) GetCurrentFrame()
	{
		lock (_lock)
		{
			return (_frontBuffer, _frontBufferValidLength);
		}
	}

	public void Clear()
	{
		lock (_lock)
		{
			_frontBuffer = null;
			_backBuffer = null;
			_frontBufferValidLength = 0;
		}
	}
}

internal class GameBackgroundSchemeHandlerFactory : ISchemeHandlerFactory
{
	public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
	{
		return new GameBackgroundResourceHandler();
	}
}

internal class GameBackgroundResourceHandler : ResourceHandler
{
	private static int _logCounter;

	public override CefReturnValue ProcessRequestAsync(IRequest request, ICallback callback)
	{
		// Parse buffer ID from URL: gamebg://localhost/frame?id=N&t=...
		int bufferId = 0;
		string url = request.Url;
		int idIdx = url.IndexOf("id=", StringComparison.Ordinal);
		if (idIdx >= 0)
		{
			idIdx += 3;
			int idEnd = url.IndexOf('&', idIdx);
			if (idEnd < 0) idEnd = url.Length;
			int.TryParse(url.AsSpan(idIdx, idEnd - idIdx), out bufferId);
		}

		var buf = GameBackgroundFrameBuffer.Get(bufferId);
		if (buf != null)
		{
			var (frame, validLength) = buf.GetCurrentFrame();
			if (frame != null && validLength > 0)
			{
				var stream = new MemoryStream(frame, 0, validLength, writable: false);
				ResponseLength = validLength;
				MimeType = "image/bmp";
				StatusCode = 200;
				Stream = stream;
				Headers.Add("Cache-Control", "no-store");
				callback.Continue();
				return CefReturnValue.Continue;
			}

			// Log why we're returning 503 (buffer exists but no data)
			int count = Interlocked.Increment(ref _logCounter);
			if (count <= 5 || count % 100 == 0)
				Console.Error.WriteLine($"SchemeHandler: bufferId={bufferId} found but no data (frame={frame != null}, validLength={validLength}) [#{count}]");
		}
		else
		{
			int count = Interlocked.Increment(ref _logCounter);
			if (count <= 5 || count % 100 == 0)
				Console.Error.WriteLine($"SchemeHandler: bufferId={bufferId} NOT FOUND in registry (url={url}) [#{count}]");
		}

		StatusCode = 503;
		MimeType = "text/plain";
		Stream = new MemoryStream(Array.Empty<byte>());
		ResponseLength = 0;
		Headers.Add("Cache-Control", "no-store");
		callback.Continue();
		return CefReturnValue.Continue;
	}
}
