using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ECommons;
using LootMastr.Automation;
using LootMastr.Data;
using LootMastr.Import;
using LootMastr.Planning;
using LootMastr.Roster;
using LootMastr.Sync;
using LootMastr.UI;
using LootMastr.UI.Tabs;

namespace LootMastr;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/lootmastr";

    private readonly WindowSystem windowSystem = new("LootMastr");
    private readonly MainWindow mainWindow;
    private readonly StaticsWindow staticsWindow;
    private readonly RaidReminder reminder;
    private readonly TiersWindow tiersWindow;
    private readonly BisImporter importer;
    private readonly SafetyGuard guard;
    private readonly ChatAnnouncer announcer;
    private readonly ObtainTracker tracker;
    private readonly ClearTracker clears;
    private readonly AddonWatcher watcher;
    private readonly LootAssignmentRunner runner;

    public Configuration Configuration { get; }
    public StaticStore Statics { get; }
    public ItemCatalog Items { get; }
    public JobCatalog Jobs { get; }
    public TierCatalog Tiers { get; }
    public GearClassifier Classifier { get; }
    public PartyReader Party { get; }
    public RosterStore Roster { get; }
    public LootPlanner Planner { get; }
    public LootWindowReader Loot { get; }
    public LootAssigner Assigner { get; }
    public EquipmentReader Equipment { get; }
    public AttributeReader Attributes { get; }
    public GearScanner Scanner { get; }
    public JobProfileCatalog Profiles { get; }
    public StatBlockBuilder Stats { get; }
    public GearComparer Gear { get; }
    public SyncClient Sync { get; }

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();
        ECommonsMain.Init(pluginInterface, this);

        Configuration = Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Before anything reads a roster or a tier. A version 1 config keeps its data at the top
        // level, and every facade below this line looks for it inside a static.
        if (Configuration.Migrate())
            Configuration.Save();

        Statics = new StaticStore(Configuration);
        Statics.OpenPrimary();

        Items = new ItemCatalog();
        Jobs = new JobCatalog();
        Tiers = new TierCatalog(Configuration, Items);
        Classifier = new GearClassifier(Tiers, Items);
        Party = new PartyReader();
        Roster = new RosterStore(Configuration, Jobs);
        importer = new BisImporter(Configuration, Classifier, Jobs, Tiers, Items);

        Profiles = new JobProfileCatalog(Jobs);
        Stats = new StatBlockBuilder(Items, Jobs);
        Gear = new GearComparer(Items, Jobs, Stats, Profiles);

        Planner = new LootPlanner(Configuration, Tiers, Roster, Gear);

        Loot = new LootWindowReader(Items, Tiers);
        guard = new SafetyGuard(Party, Loot);
        runner = new LootAssignmentRunner(Configuration, guard);
        Assigner = new LootAssigner(Configuration, Loot, Planner, Roster, guard, Tiers, runner);
        announcer = new ChatAnnouncer(Configuration, guard);
        tracker = new ObtainTracker(Configuration, Roster, Tiers, Assigner);
        clears = new ClearTracker(Configuration, Tiers, Roster, Party);

        Sync = new SyncClient(Configuration, Statics, Tiers);
        reminder = new RaidReminder(Configuration, Statics);

        Equipment = new EquipmentReader(Items);
        Attributes = new AttributeReader();
        Scanner = new GearScanner(Configuration, Roster, Jobs, Party, Equipment, Attributes, Classifier);
        watcher = new AddonWatcher();

        var tabs = new List<ITab>
        {
            new LootTab(Configuration, Assigner, announcer, Roster, Tiers, Planner, clears),
            new RosterTab(Configuration, Roster, Jobs, importer, Tiers, Scanner, Items, Gear, Planner),
            new PlanTab(Configuration, Roster, Planner, Tiers),
            new DebugTab(Loot, Party, Tiers, tracker, watcher, Items, Jobs, Configuration),
            new SettingsTab(Configuration, Roster, Planner),
        };

        staticsWindow = new StaticsWindow(Configuration, Statics, Roster, Jobs, Party, Sync);
        tiersWindow = new TiersWindow(Configuration, Tiers, Items, Statics, Sync);
        mainWindow = new MainWindow(tabs, Statics, Tiers, Sync, Configuration,
                                    staticsWindow.Open, tiersWindow.Open);

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(staticsWindow);
        windowSystem.AddWindow(tiersWindow);

        Services.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open LootMastr. Also: /lootmastr roster, /lootmastr statics, " +
                          "/lootmastr tier, /lootmastr settings.",
        });

        Services.PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        Services.PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Services.PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "settings":
            case "config":
                OpenSettings();
                break;

            case "roster":
                mainWindow.OpenAt("roster");
                break;

            case "statics":
            case "static":
                staticsWindow.Open();
                break;

            case "tier":
            case "tiers":
                tiersWindow.Open();
                break;

            case "plan":
                mainWindow.OpenAt("plan");
                break;

            case "loot":
                mainWindow.OpenAt("loot");
                break;

            case "scan":
            case "gear":
                Services.Chat.Print($"LootMastr: {Scanner.Start()}");
                break;

            case "stop":
                Scanner.Stop("Stopped by command.");
                Services.Chat.Print("LootMastr: stopped.");
                break;

            default:
                ToggleMainUi();
                break;
        }
    }

    private void ToggleMainUi() => mainWindow.Toggle();

    private void OpenSettings() => mainWindow.OpenAt("settings");

    public void Dispose()
    {
        Services.PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        Services.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        Services.PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;

        Services.Commands.RemoveHandler(CommandName);

        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        staticsWindow.Dispose();
        tiersWindow.Dispose();
        Sync.Dispose();
        reminder.Dispose();
        importer.Dispose();
        tracker.Dispose();
        clears.Dispose();
        Scanner.Dispose();
        watcher.Dispose();
        runner.Dispose();

        ECommonsMain.Dispose();
    }
}
