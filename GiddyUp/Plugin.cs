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
    [PluginService] internal static IToastGui               Toast           { get; private set; } = null!;
    [PluginService] internal static IGameGui                GameGui         { get; private set; } = null!;

    internal Configuration Config { get; }
    private static Configuration? _cfg;
    internal static bool DetailedLog => _cfg?.DetailedLog == true || PluginInterface.IsDev;

    /// <summary>What Giddy Up does and why: Information when the detailed log is on (always in dev builds), else Debug.</summary>
    internal static void Info(string message)
    {
        if (DetailedLog) Log.Information(message);
        else Log.Debug(message);
    }

    internal readonly Speeds Speeds;
    internal readonly AttackRange Range;
    internal readonly OnFoot OnFoot;
    internal readonly Mounted Mounted;
    internal readonly ActionDismount ActionDismount;
    internal readonly Link Link;

    private readonly WindowSystem _windows = new("GiddyUp");
    private readonly ConfigWindow _configWindow;
    private bool _configDirty;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _cfg   = Config;
        if (Config.Version < 2)
        {
            Config.HoldStillNotice = true; // now on by default: switch it on once for test builds that had it off
            Config.Version = 2;
            PluginInterface.SavePluginConfig(Config);
        }

        Speeds         = new Speeds(this);
        Range          = new AttackRange { MeleeAtMelee = Config.MeleeAtMeleeRange };
        OnFoot         = new OnFoot(this);
        Mounted        = new Mounted(this);
        ActionDismount = new ActionDismount(this);
        Link           = new Link(this);

        _configWindow = new ConfigWindow(this);
        _windows.AddWindow(_configWindow);
        PluginInterface.UiBuilder.Draw         += _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   += _configWindow.Toggle;

        // Everything starts from these events; nothing runs every frame unless a feature needs it right now.
        Condition.ConditionChange    += OnConditionChange;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        ClientState.ClassJobChanged  += OnClassJobChanged;
        ClientState.Login            += OnLogin;
        ClientState.Logout           += OnLogout;

        Commands.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = "Open the settings." });

        // Plugin loaded while already in game.
        Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                LoggedIn = ClientState.IsLoggedIn;
                Refresh();
            }
            catch (Exception e)
            {
                Log.Error(e, $"Start-up failed");
            }
        });
    }

    /// <summary>From the login/logout events (more reliable than IsLoggedIn during the logout event itself).</summary>
    internal bool LoggedIn { get; private set; }

    public void Dispose()
    {
        Condition.ConditionChange    -= OnConditionChange;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        ClientState.ClassJobChanged  -= OnClassJobChanged;
        ClientState.Login            -= OnLogin;
        ClientState.Logout           -= OnLogout;
        Commands.RemoveHandler(Command);
        Link.Dispose();
        ActionDismount.Dispose();
        Mounted.Dispose();
        OnFoot.Dispose();
        SaveIfDirty(now: true);
        PluginInterface.UiBuilder.Draw         -= _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   -= _configWindow.Toggle;
        _windows.RemoveAllWindows();
    }

    internal void SaveConfig()
    {
        PluginInterface.SavePluginConfig(Config);
        _configDirty = false;
        Range.MeleeAtMelee = Config.MeleeAtMeleeRange; // recomputed at the next check if it changed
        Range.Ensure();
        Refresh();
    }

    /// <summary>Learned values changed: saved at a calm moment (a condition change, at most every 30 s), not per sample.</summary>
    internal void MarkDirty() => _configDirty = true;

    private long _savedAt;

    private void SaveIfDirty(bool now = false)
    {
        if (!_configDirty || (!now && Environment.TickCount64 - _savedAt < 30_000))
            return;
        PluginInterface.SavePluginConfig(Config);
        _configDirty = false;
        _savedAt = Environment.TickCount64;
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

    // Attack range: the on-foot watcher notices job/level changes itself (one compare per tick) and the game-data scan
    // runs in the background; these only make sure the right watchers run.
    private void OnClassJobChanged(uint classJobId)
    {
        Range.Prefetch(classJobId); // the event's job: the character may still report the old one
        Refresh();
    }

    private void OnLogin()
    {
        LoggedIn = true;
        Refresh();
    }

    private void OnLogout(int type, int code)
    {
        LoggedIn = false;
        OnFoot.Reset();
        Mounted.Reset();
        ActionDismount.Reset();
        SaveIfDirty(now: true);
        Refresh();
    }

    private void OnCommand(string command, string args) => _configWindow.Toggle();
}
