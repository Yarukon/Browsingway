using Browsingway.Common;
using Dalamud.Interface;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ImGuiNET;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Browsingway;

// ReSharper disable once ClassNeverInstantiated.Global
internal class Settings : IDisposable
{
	public event EventHandler<InlayConfiguration>? InlayAdded;
	public event EventHandler<InlayConfiguration>? InlayNavigated;
	public event EventHandler<InlayConfiguration>? InlayDebugged;
	public event EventHandler<InlayConfiguration>? InlayRemoved;
	public event EventHandler<InlayConfiguration>? InlayZoomed;
	public event EventHandler<InlayConfiguration>? InlayMuted;
	public event EventHandler? TransportChanged;

	public readonly Configuration Config;

	[PluginService]
	// ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
	private static DalamudPluginInterface PluginInterface { get; set; } = null!;

	[PluginService]
	// ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
	private static IChatGui Chat { get; set; } = null!;

	private bool _actAvailable = false;

#if DEBUG
	private bool _open = true;
#else
	private bool _open;
#endif

	private List<FrameTransportMode> _availableTransports = new();

	private InlayConfiguration? _selectedInlay;
	private Timer? _saveDebounceTimer;

	public Settings()
	{
		PluginInterface.UiBuilder.OpenConfigUi += () => _open = true;
		Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
	}

	public void Dispose() { }

	public void OnActAvailabilityChanged(bool available)
	{
		_actAvailable = available;
		foreach (InlayConfiguration? inlayConfig in Config.Inlays)
		{
			if (inlayConfig is { ActOptimizations: true, Disabled: false })
			{
				if (_actAvailable)
					InlayAdded?.Invoke(this, inlayConfig);
				else
					InlayRemoved?.Invoke(this, inlayConfig);
			}
		}
	}

	public void HandleConfigCommand(string rawArgs)
	{
		_open = true;

		// TODO: Add further config handling if required here.
	}

	public void HandleRefreshAllCommand()
	{
		if (Config.Inlays.Count > 0)
		{
			foreach (InlayConfiguration? inlayConfig in Config.Inlays)
			{
				ReloadInlay(inlayConfig);
			}
			Chat.Print("已向所有嵌入式窗口发送刷新操作!");
		} else
		{
			Chat.PrintError("你还没有创建嵌入式窗口!");
		}
	}

	public void HandleInlayCommand(string rawArgs)
	{
		string[] args = rawArgs.Split(null as char[], 3, StringSplitOptions.RemoveEmptyEntries);

		// Ensure there's enough arguments
		if (args.Length < 2 || (args[1] != "reload" && args.Length < 3))
		{
			Chat.PrintError("无效嵌入式窗口指令. 支持的参数: '[inlayCommandName] [setting] [value]'");
			return;
		}

		// Find the matching inlay config
		InlayConfiguration? targetConfig = Config.Inlays.Find(inlay => GetInlayCommandName(inlay) == args[0]);
		if (targetConfig == null)
		{
			Chat.PrintError(
				$"未知嵌入式窗口 '{args[0]}'.");
			return;
		}

		switch (args[1])
		{
			case "url":
				CommandSettingString(args[2], ref targetConfig.Url);
				// TODO: This call is duped with imgui handling. DRY.
				NavigateInlay(targetConfig);
				break;
			case "locked":
				CommandSettingBoolean(args[2], ref targetConfig.Locked);
				break;
			case "hidden":
				CommandSettingBoolean(args[2], ref targetConfig.Hidden);
				break;
			case "typethrough":
				CommandSettingBoolean(args[2], ref targetConfig.TypeThrough);
				break;
			case "clickthrough":
				CommandSettingBoolean(args[2], ref targetConfig.ClickThrough);
				break;
			case "muted":
				CommandSettingBoolean(args[2], ref targetConfig.Muted);
				break;
			case "disabled":
				CommandSettingBoolean(args[2], ref targetConfig.Disabled);
				break;
			case "act":
				CommandSettingBoolean(args[2], ref targetConfig.ActOptimizations);
				break;
			case "reload":
				ReloadInlay(targetConfig);
				break;

			default:
				Chat.PrintError(
					$"未知设定 '{args[1]}. 有效设定为: url,hidden,locked,clickthrough,typethrough,muted,disabled,act.");
				return;
		}

		SaveSettings();
	}

	private void CommandSettingString(string value, ref string target)
	{
		target = value;
	}

	private void CommandSettingBoolean(string value, ref bool target)
	{
		switch (value)
		{
			case "on":
				target = true;
				break;
			case "off":
				target = false;
				break;
			case "toggle":
				target = !target;
				break;
			default:
				Chat.PrintError(
					$"无效布尔值 '{value}. 可用参数有: on,off,toggle.");
				break;
		}
	}

	public void SetAvailableTransports(FrameTransportMode transports)
	{
		// Decode bit flags to array for easier ui crap
		_availableTransports = Enum.GetValues(typeof(FrameTransportMode))
			.Cast<FrameTransportMode>()
			.Where(transport => transport != FrameTransportMode.None && transports.HasFlag(transport))
			.ToList();

		// If the configured transport isn't available, pick the first so we don't end up in a weird spot.
		// NOTE: Might be nice to avoid saving this to disc - a one-off failure may cause a save of full fallback mode.
		if (_availableTransports.Count > 0 && !_availableTransports.Contains(Config.FrameTransportMode))
		{
			SetActiveTransport(_availableTransports[0]);
		}
	}

	public void HydrateInlays()
	{
		// Hydrate any inlays in the config
		foreach (InlayConfiguration? inlayConfig in Config.Inlays)
		{
			if (!inlayConfig.Disabled && (!inlayConfig.ActOptimizations || _actAvailable))
			{
				InlayAdded?.Invoke(this, inlayConfig);
			}
		}
	}

	private InlayConfiguration? AddNewInlay()
	{
		InlayConfiguration? inlayConfig = new() { Guid = Guid.NewGuid(), Name = "新的嵌入式窗口", Url = "about:blank" };
		Config.Inlays.Add(inlayConfig);
		InlayAdded?.Invoke(this, inlayConfig);
		SaveSettings();

		return inlayConfig;
	}

	private void NavigateInlay(InlayConfiguration inlayConfig)
	{
		if (inlayConfig.Url == "") { inlayConfig.Url = "about:blank"; }

		InlayNavigated?.Invoke(this, inlayConfig);
	}

	private void UpdateZoomInlay(InlayConfiguration inlayConfig)
	{
		InlayZoomed?.Invoke(this, inlayConfig);
	}

	private void UpdateMuteInlay(InlayConfiguration inlayConfig)
	{
		InlayMuted?.Invoke(this, inlayConfig);
	}

	private void ReloadInlay(InlayConfiguration inlayConfig) { NavigateInlay(inlayConfig); }

	private void DebugInlay(InlayConfiguration inlayConfig)
	{
		InlayDebugged?.Invoke(this, inlayConfig);
	}

	private void RemoveInlay(InlayConfiguration inlayConfig)
	{
		InlayRemoved?.Invoke(this, inlayConfig);
		Config.Inlays.Remove(inlayConfig);
		SaveSettings();
	}

	private void SetActiveTransport(FrameTransportMode transport)
	{
		Config.FrameTransportMode = transport;
		TransportChanged?.Invoke(this, EventArgs.Empty);
	}

	private void DebouncedSaveSettings()
	{
		_saveDebounceTimer?.Dispose();
		_saveDebounceTimer = new Timer(_ => SaveSettings(), null, 1000, Timeout.Infinite);
	}

	private void SaveSettings()
	{
		_saveDebounceTimer?.Dispose();
		_saveDebounceTimer = null;
		PluginInterface.SavePluginConfig(Config);
	}

	private string GetInlayCommandName(InlayConfiguration inlayConfig)
	{
		return Regex.Replace(inlayConfig.Name, @"\s+", "").ToLower();
	}

	public void Render()
	{
		if (!_open) { return; }

		// Primary window container
		ImGui.SetNextWindowSizeConstraints(new Vector2(400, 300), new Vector2(9001, 9001));
		ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None
		                               | ImGuiWindowFlags.NoScrollbar
		                               | ImGuiWindowFlags.NoScrollWithMouse
		                               | ImGuiWindowFlags.NoCollapse;
		ImGui.Begin("Browsingway 设置", ref _open, windowFlags);

		RenderPaneSelector();

		// Pane details
		bool dirty = false;
		ImGui.SameLine();
		ImGui.BeginChild("details");
		if (_selectedInlay == null)
		{
			dirty |= RenderGeneralSettings();
		}
		else
		{
			dirty |= RenderInlaySettings(_selectedInlay);
		}

		ImGui.EndChild();

		if (dirty) { DebouncedSaveSettings(); }

		ImGui.End();
	}

	private void RenderPaneSelector()
	{
		// Selector pane
		ImGui.BeginGroup();
		ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));

		int selectorWidth = 100;
		ImGui.BeginChild("panes", new Vector2(selectorWidth, -ImGui.GetFrameHeightWithSpacing()), true);

		// General settings
		if (ImGui.Selectable("通常", _selectedInlay == null))
		{
			_selectedInlay = null;
		}

		// Inlay selector list
		ImGui.Dummy(new Vector2(0, 5));
		ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
		ImGui.Text("- 嵌入式窗口 -");
		ImGui.PopStyleVar();
		foreach (InlayConfiguration? inlayConfig in Config?.Inlays!)
		{
			if (ImGui.Selectable($"{inlayConfig.Name}##{inlayConfig.Guid}", _selectedInlay == inlayConfig))
			{
				_selectedInlay = inlayConfig;
			}
		}

		ImGui.EndChild();

		// Selector controls
		ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0);
		ImGui.PushFont(UiBuilder.IconFont);

		int buttonWidth = selectorWidth / 2;
		if (ImGui.Button(FontAwesomeIcon.Plus.ToIconString(), new Vector2(buttonWidth, 0)))
		{
			_selectedInlay = AddNewInlay();
		}

		ImGui.SameLine();
		if (_selectedInlay != null)
		{
			if (ImGui.Button(FontAwesomeIcon.Trash.ToIconString(), new Vector2(buttonWidth, 0)))
			{
				InlayConfiguration? toRemove = _selectedInlay;
				_selectedInlay = null;
				RemoveInlay(toRemove);
			}
		}
		else
		{
			ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
			ImGui.Button(FontAwesomeIcon.Trash.ToIconString(), new Vector2(buttonWidth, 0));
			ImGui.PopStyleVar();
		}

		ImGui.PopFont();
		ImGui.PopStyleVar(2);

		ImGui.EndGroup();
	}

	private bool RenderGeneralSettings()
	{
		bool dirty = false;

		ImGui.Text("在左侧选择一个嵌入式窗口来更改设置.");

		if (ImGui.CollapsingHeader("指令帮助", ImGuiTreeNodeFlags.DefaultOpen))
		{
			// TODO: If this ever gets more than a few options, should probably colocate help with the defintion. Attributes?
			ImGui.Text("/bw config");
			ImGui.Text("打开配置窗口.");
			ImGui.Text("/bw refresh");
			ImGui.Text("刷新所有的嵌入式窗口.");
			ImGui.Dummy(new Vector2(0, 5));
			ImGui.Text("/bw inlay [inlayCommandName] [setting] [value]");
			ImGui.TextWrapped(
				"更改一个嵌入式窗口的设置.\n" +
				"\tinlayCommandName: 要编辑的窗口. 使用 '指令名称' 来显示它的当前设定.\n" +
				"\tsetting: 要更改的设定. 支持的设定有:\n" +
				"\t\turl: string\n" +
				"\t\tdisabled: boolean\n" +
				"\t\tmuted: boolean\n" +
				"\t\tact: boolean\n" +
				"\t\tlocked: boolean\n" +
				"\t\thidden: boolean\n" +
				"\t\ttypethrough: boolean\n" +
				"\t\tclickthrough: boolean\n" +
				"\t\treload: -\n" +
				"\tvalue: 要设置的值. 支持的值有:\n" +
				"\t\tstring: 任何字符串\n\t\tboolean: on, off, toggle");
		}

		if (ImGui.CollapsingHeader("高级设置"))
		{
			IEnumerable<string> options = _availableTransports.Select(transport => transport.ToString());
			int currentIndex = _availableTransports.IndexOf(Config.FrameTransportMode);

			if (_availableTransports.Count == 0)
			{
				options = options.Append("初始化...");
				currentIndex = 0;
			}

			if (options.Count() <= 1) { ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f); }

			bool transportChanged = ImGui.Combo("帧传输模式", ref currentIndex, options.ToArray(), options.Count());
			if (options.Count() <= 1) { ImGui.PopStyleVar(); }

			// TODO: Flipping this should probably try to rebuild existing inlays
			dirty |= transportChanged;
			if (transportChanged)
			{
				SetActiveTransport(_availableTransports[currentIndex]);
			}

			if (Config.FrameTransportMode == FrameTransportMode.BitmapBuffer)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF);
				ImGui.TextWrapped("位图缓冲区帧传输是一种后备功能, 只有在没有其他选项适合你的情况下才使用. 它不如共享纹理选项稳定.");
				ImGui.PopStyleColor();
			}
		}

		return dirty;
	}

	private bool RenderInlaySettings(InlayConfiguration inlayConfig)
	{
		bool dirty = false;

		ImGui.PushID(inlayConfig.Guid.ToString());

		dirty |= ImGui.InputText("名称", ref inlayConfig.Name, 100);

		ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
		string? commandName = GetInlayCommandName(inlayConfig);
		ImGui.InputText("指令名称", ref commandName, 100);
		ImGui.PopStyleVar();

		dirty |= ImGui.InputText("URL", ref inlayConfig.Url, 1000);
		if (ImGui.IsItemDeactivatedAfterEdit()) { NavigateInlay(inlayConfig); }

		if (ImGui.InputFloat("缩放", ref inlayConfig.Zoom, 1f, 10f, "%.0f%%"))
		{
			// clamp to allowed range 
			if (inlayConfig.Zoom < 10f)
			{
				inlayConfig.Zoom = 10f;
			}
			else if (inlayConfig.Zoom > 500f)
			{
				inlayConfig.Zoom = 500f;
			}

			dirty = true;

			// notify of zoom change
			UpdateZoomInlay(inlayConfig);
		}

		if (ImGui.InputFloat("透明度", ref inlayConfig.Opacity, 1f, 10f, "%.0f%%"))
		{
			// clamp to allowed range 
			if (inlayConfig.Opacity < 10f)
			{
				inlayConfig.Opacity = 10f;
			}
			else if (inlayConfig.Opacity > 100f)
			{
				inlayConfig.Opacity = 100f;
			}

			dirty = true;
		}

		if (ImGui.InputInt("帧率", ref inlayConfig.Framerate, 1, 10))
		{
			// clamp to allowed range 
			if (inlayConfig.Framerate < 1)
			{
				inlayConfig.Framerate = 1;
			}
			else if (inlayConfig.Framerate > 300)
			{
				inlayConfig.Framerate = 300;
			}

			dirty = true;

			// framerate changes require the recreation of the browser instance
			// TODO: this is ugly as heck, fix once proper IPC is up and running
			InlayRemoved?.Invoke(this, inlayConfig);
			InlayAdded?.Invoke(this, inlayConfig);
		}

		ImGui.SetNextItemWidth(100);
		ImGui.Columns(2, "boolInlayOptions", false);

		if (ImGui.Checkbox("禁用", ref inlayConfig.Disabled))
		{
			if (inlayConfig.Disabled)
				InlayRemoved?.Invoke(this, inlayConfig);
			else
				InlayAdded?.Invoke(this, inlayConfig);
			dirty = true;
		}

		if (ImGui.IsItemHovered()) { ImGui.SetTooltip("禁用嵌入式窗口. 与仅隐藏相反, 该设定会防止此嵌入式窗口被创建."); }

		ImGui.NextColumn();
		ImGui.NextColumn();


		if (ImGui.Checkbox("静音", ref inlayConfig.Muted))
		{
			UpdateMuteInlay(inlayConfig);
			dirty = true;
		}

		if (ImGui.IsItemHovered()) { ImGui.SetTooltip("启用或禁用音频播放."); }

		ImGui.NextColumn();
		ImGui.NextColumn();

		if (ImGui.Checkbox("ACT/IINACT 优化", ref inlayConfig.ActOptimizations))
		{
			if (!inlayConfig.Disabled)
			{
				if (inlayConfig.ActOptimizations)
				{
					if (!_actAvailable)
						InlayRemoved?.Invoke(this, inlayConfig);
					else
						InlayAdded?.Invoke(this, inlayConfig);
				}
				else
				{
					InlayAdded?.Invoke(this, inlayConfig);
				}
			}

			dirty = true;
		}

		if (ImGui.IsItemHovered()) { ImGui.SetTooltip("启用针对 ACT/IINACT 的特殊优化. 如果 ACT/IINACT 未在运行的话将不会渲染该嵌入式窗口."); }

		ImGui.NextColumn();
		ImGui.NextColumn();

		if (inlayConfig.ClickThrough) { ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f); }

		bool true_ = true;
		dirty |= ImGui.Checkbox("锁定窗口", ref inlayConfig.ClickThrough ? ref true_ : ref inlayConfig.Locked);
		if (inlayConfig.ClickThrough) { ImGui.PopStyleVar(); }

		if (ImGui.IsItemHovered()) { ImGui.SetTooltip("防止窗口被拖动或调整大小. 会被点击穿透进行隐式设置."); }

		ImGui.NextColumn();

		dirty |= ImGui.Checkbox("隐藏", ref inlayConfig.Hidden);
		if (ImGui.IsItemHovered()) { ImGui.SetTooltip("隐藏窗口. 这不会阻止窗口继续运行，只会停止渲染."); }

		ImGui.NextColumn();

		if (inlayConfig.ClickThrough) { ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f); }

		dirty |= ImGui.Checkbox("输入穿透", ref inlayConfig.ClickThrough ? ref true_ : ref inlayConfig.TypeThrough);
		if (inlayConfig.ClickThrough) { ImGui.PopStyleVar(); }

		if (ImGui.IsItemHovered()) { ImGui.SetTooltip("防止窗口被任何键盘事件影响. 会被点击穿透进行隐式设置."); }

		ImGui.NextColumn();

		dirty |= ImGui.Checkbox("点击穿透", ref inlayConfig.ClickThrough);
		if (ImGui.IsItemHovered()) { ImGui.SetTooltip("防止窗口被任何鼠标事件影响. 会隐式设置输入穿透和锁定窗口."); }

		ImGui.NextColumn();

		ImGui.Columns(1);

		if (ImGui.Button("刷新")) { ReloadInlay(inlayConfig); }

		ImGui.SameLine();
		if (ImGui.Button("打开开发者工具")) { DebugInlay(inlayConfig); }

		ImGui.PopID();

		return dirty;
	}
}