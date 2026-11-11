using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GiddyUp;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/giddyup";
    internal const string Tag = "[GiddyUp]";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IDataManager            Data            { get; private set; } = null!;
    [PluginService] internal static ICommandManager         Commands        { get; private set; } = null!;
    [PluginService] internal static IChatGui                Chat            { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState     { get; private set; } = null!;
    [PluginService] internal static ICondition              Condition       { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework       { get; private set; } = null!;
    [PluginService] internal static IObjectTable            ObjectTable     { get; private set; } = null!;
    [PluginService] internal static ITargetManager          TargetManager   { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider    Hooks           { get; private set; } = null!;

    internal Configuration Config { get; }
    private static Configuration? _cfg;
    internal static bool DetailedLog => _cfg?.DetailedLog == true || PluginInterface.IsDev;

    /// <summary>What Giddy Up does and why: Information when the detailed log is on (always in dev builds), else Debug.</summary>
    internal static void Info(string message)
    {
        if (DetailedLog) Log.Information($"{Tag} {message}");
        else Log.Debug($"{Tag} {message}");
    }

    internal readonly Speeds Speeds;
    internal readonly AttackRange Range;
    internal readonly OnFoot OnFoot;
    internal readonly Mounted Mounted;
    internal readonly ActionDismount ActionDismount;

    private readonly WindowSystem _windows = new("GiddyUp");
    private readonly ConfigWindow _configWindow;
    private bool _configDirty;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _cfg   = Config;

        Speeds         = new Speeds(this);
        Range          = new AttackRange();
        OnFoot         = new OnFoot(this);
        Mounted        = new Mounted(this);
        ActionDismount = new ActionDismount(this);

        _configWindow = new ConfigWindow(this);
        _windows.AddWindow(_configWindow);
        PluginInterface.UiBuilder.Draw         += _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   += _configWindow.Toggle;

        // Everything starts from these events; nothing runs every frame unless a feature needs it right now.
        Condition.ConditionChange    += OnConditionChange;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        ClientState.ClassJobChanged  += OnClassJobChanged;
        ClientState.LevelChanged     += OnLevelChanged;
        ClientState.Login            += OnLogin;
        ClientState.Logout           += OnLogout;

        Commands.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = "Open the settings." });

        // Plugin loaded while already in game.
        Framework.RunOnFrameworkThread(() =>
        {
            if (!ClientState.IsLoggedIn)
                return;
            Range.Invalidate();
            OnFoot.OnTerritory(ClientState.TerritoryType);
            Refresh();
        });
    }

    public void Dispose()
    {
        Condition.ConditionChange    -= OnConditionChange;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        ClientState.ClassJobChanged  -= OnClassJobChanged;
        ClientState.LevelChanged     -= OnLevelChanged;
        ClientState.Login            -= OnLogin;
        ClientState.Logout           -= OnLogout;
        Commands.RemoveHandler(Command);
        ActionDismount.Dispose();
        Mounted.Dispose();
        OnFoot.Dispose();
        SaveIfDirty();
        PluginInterface.UiBuilder.Draw         -= _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   -= _configWindow.Toggle;
        _windows.RemoveAllWindows();
    }

    internal void SaveConfig()
    {
        PluginInterface.SavePluginConfig(Config);
        _configDirty = false;
        Refresh();
    }

    /// <summary>Learned values changed: saved at the next calm moment (condition change), not on every sample.</summary>
    internal void MarkDirty() => _configDirty = true;

    private void SaveIfDirty()
    {
        if (_configDirty)
            PluginInterface.SavePluginConfig(Config);
        _configDirty = false;
    }

    /// <summary>Arms or disarms the per-frame watchers for the current state.</summary>
    internal void Refresh()
    {
        Mounted.Refresh();
        OnFoot.Refresh();
    }

    private void OnConditionChange(Dalamud.Game.ClientState.Conditions.ConditionFlag flag, bool value)
    {
        OnFoot.OnCondition(flag, value);
        Mounted.OnCondition(flag, value);
        ActionDismount.OnCondition(flag, value);
        Refresh();
        SaveIfDirty();
    }

    private void OnTerritoryChanged(uint territory)
    {
        OnFoot.OnTerritory(territory);
        Refresh();
    }

    private void OnClassJobChanged(uint classJobId)
    {
        Range.Invalidate();
        Refresh();
    }

    private void OnLevelChanged(uint classJobId, uint level) => Range.Invalidate();

    private void OnLogin()
    {
        Range.Invalidate();
        Refresh();
    }

    private void OnLogout(int type, int code)
    {
        SaveIfDirty();
        Refresh();
    }

    private void OnCommand(string command, string args) => _configWindow.Toggle();
}
