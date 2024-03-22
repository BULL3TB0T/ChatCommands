using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using ChatCommands;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.Input;

namespace ChatCommands
{
    public static class ChatUtility
    {
        public static string Color(Color color) => $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>";
        public static string Color(string hexCode) => $"<color=#{hexCode}>";
        public static string Simplify(string name) => string.Join(" ",
            name.Split('_').Select(subName => char.ToUpper(subName[0]) + subName.Substring(1).ToLower()));
    }

    public class ChatPlugin
    {
        public string guid;
        public Action updateAction;

        public ChatPlugin(string guid) => this.guid = guid;

        public ChatPlugin(string guid, Action updateAction)
        {
            this.guid = guid;
            this.updateAction = updateAction;
        }
    }

    public class ChatMessage
    {
        public object text;
        public string name;
        public Color color;
        public int size;
        public DateTime time;

        public ChatMessage(object text, string name = null, Color color = default, int size = -1, DateTime time = default)
        {
            this.text = text;
            this.name = name.IsNullOrWhiteSpace() ? "System" : name;
            this.color = color == default ? new Color(1, 1, 1) : color;
            this.size = size == -1 ? ChatCommandsPlugin.configSize.Value : size;
            this.time = time == default ? DateTime.Now : time;
        }
    }

    public class ChatCommand
    {
        public string name;
        public string description;
        public string example;
        public ChatPlugin plugin;
        private Action<string, string[]> executeAction1;
        private Action<string> executeAction2;

        public ChatCommand(string name, Action<string, string[]> executeAction1)
        {
            this.name = name.Replace(' ', '_');
            this.executeAction1 = executeAction1;
        }

        public ChatCommand(string name, string description, Action<string, string[]> executeAction1)
        {
            this.name = name.Replace(' ', '_');
            this.description = description;
            this.executeAction1 = executeAction1;
        }

        public ChatCommand(string name, string description, string example, Action<string, string[]> executeAction1)
        {
            this.name = name.Replace(' ', '_');
            this.description = description;
            this.example = example;
            this.executeAction1 = executeAction1;
        }

        public ChatCommand(string name, Action<string> executeAction2)
        {
            this.name = name.Replace(' ', '_');
            this.executeAction2 = executeAction2;
        }

        public ChatCommand(string name, string description, Action<string> executeAction2)
        {
            this.name = name.Replace(' ', '_');
            this.description = description;
            this.executeAction2 = executeAction2;
        }

        public ChatCommand(string name, string description, string example, Action<string> executeAction2)
        {
            this.name = name.Replace(' ', '_');
            this.description = description;
            this.example = example;
            this.executeAction2 = executeAction2;
        }

        public void Execute(string[] args)
        {
            try
            {
                executeAction1?.Invoke(name, args);
                executeAction2?.Invoke(name);
            }
            catch (Exception e)
            {
                ChatManager.instance.Message(e.ToString(), ChatUtility.Simplify(name), new Color(1, 0, 0));
            }
        }
    }

    public class ChatManager : MonoBehaviour
    {
        public static ChatManager instance;
        private AssetBundle assetBundle;
        public delegate (ChatPlugin, ChatCommand[]) onInitHandler();
        public static event onInitHandler onInit;
        private bool isInitialized = false;
        private bool isCreatingUI = false;

        private List<ChatPlugin> plugins = new List<ChatPlugin>();
        private List<ChatCommand> commands = new List<ChatCommand>();

        private GameObject chatWindow;
        private Canvas canvas;
        private Transform content;
        private TMP_InputField inputField;
        private ScrollRect scrollRect;
        private TMP_Text autoComplete;

        private List<string> previousPrompts = new List<string>();
        private int selectedPrompt;

        private List<ChatMessage> previousTexts = new List<ChatMessage>();
        private GameObject textPrefab;

        private GameObject tempSelected;
        private CursorLockMode tempCursor;

        private ChatCommand closestCmd;

        void Awake()
        {
            if (instance == null) instance = this;
            else
            {
                Destroy(gameObject);
                return;
            }
            commands.AddRange(new ChatCommand[]
            {
                new ChatCommand("help",
                    "Shows a list of commands.",
                    "?command(string)",
                    (string name, string[] args) =>
                    {
                        StringBuilder sb = new StringBuilder();
                        if (args.Length >= 1)
                        {
                            ChatCommand cmd = commands.FirstOrDefault(x => x.name == args[0]);
                            if (cmd != null)
                            {
                                sb.Append($"\nName: {cmd.name}");
                                if (cmd.description != null) sb.Append($"\nDescription: {cmd.description}");
                                if (cmd.plugin != null) sb.Append($"\nPlugin: {cmd.plugin.guid}");
                                if (cmd.example != null)
                                {
                                    sb.Append($"\nExample: {cmd.name} {cmd.example}");
                                    if (cmd.example.Contains("?")) sb.Append($"\n{ChatUtility.Color(Color.yellow)}? means optional.");
                                    if (cmd.example.Contains("#")) sb.Append($"\n{ChatUtility.Color(Color.yellow)}# means arguments are connected after this.");
                                    if (cmd.example.Contains("(") && cmd.example.Contains(")"))
                                        sb.Append($"\n{ChatUtility.Color(Color.yellow)}() means what type the argument is.");
                                    if (cmd.example.Contains("{") && cmd.example.Contains("}"))
                                        sb.Append($"\n{ChatUtility.Color(Color.yellow)}{{}} means comment.");
                                    if (cmd.example.Contains("[") && cmd.example.Contains("]"))
                                        sb.Append($"\n{ChatUtility.Color(Color.yellow)}[] means how the argument should be.");
                                }
                            }
                            else
                                sb.Append($"Command called '{args[0]}' does not exist.");
                        }
                        else
                        {
                            sb.Append($"\nCommands ({commands.Count}):\n");
                            commands.ForEach(x => sb.Append($"\n{x.name}"));
                        }
                        Message(sb.ToString(), ChatUtility.Simplify(name));
                    }),
                new ChatCommand("clear",
                    "Clears all the messages.",
                    name =>
                    {
                        previousTexts.Clear();
                        foreach (Transform message in content) Destroy(message.gameObject);
                        Message("Messages has been cleared!", ChatUtility.Simplify(name));
                    }),
                new ChatCommand("plugins",
                    "Shows a list of plugins.",
                    name =>
                    {
                        StringBuilder sb = new StringBuilder();
                        BepInPlugin[] pluginInfos = Chainloader.PluginInfos.Values.Select(x => x.Metadata).ToArray();
                        sb.Append($"\nPlugins ({pluginInfos.Length}):\n");
                        for (int i = 0; i < pluginInfos.Length; i++)
                        {
                            BepInPlugin pluginInfo = pluginInfos[i];
                            sb.Append($"\nName: {pluginInfo.Name}\n" +
                                $"GUID: {pluginInfo.GUID}\n" +
                                $"Version: {pluginInfo.Version}\n" +
                                $"Dependency: {(plugins.Any(x => x.guid == pluginInfo.GUID) ? "Yes" : "No")}{(i != (commands.Count - 1) ? "\n" : null)}");
                        }
                        Message(sb.ToString(), ChatUtility.Simplify(name));
                    })
            });
            CreateUI();
            SceneManager.activeSceneChanged += delegate { CreateUI(); };
        }

        void Update()
        {
            if (!isInitialized || isCreatingUI) return;
            if (ChatCommandsPlugin.configModifierEnabled.Value
                ?
                (InputManager.GetKey(ChatCommandsPlugin.configModifier.Value) && InputManager.GetKeyDown(ChatCommandsPlugin.configToggle.Value))
                :
                InputManager.GetKeyDown(ChatCommandsPlugin.configToggle.Value)
                )
            {
                canvas.enabled = !canvas.enabled;
                if (canvas.enabled)
                {
                    selectedPrompt = -1;
                    inputField.text = null;
                    autoComplete.text = null;
                    tempSelected = EventSystem.current.currentSelectedGameObject;
                    tempCursor = Cursor.lockState;
                    inputField.Select();
                    ScrollToBottom();
                }
                else
                {
                    EventSystem.current.SetSelectedGameObject(tempSelected);
                    tempSelected = null;
                    Cursor.lockState = tempCursor;
                    tempCursor = default;
                }
            }
            if (canvas.enabled)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                inputField.ActivateInputField();
                bool upArrow = InputManager.GetKeyDown(KeyCode.UpArrow);
                bool downArrow = InputManager.GetKeyDown(KeyCode.DownArrow);
                if (upArrow || downArrow)
                {
                    if (upArrow && selectedPrompt < (previousPrompts.Count - 1)) selectedPrompt++;
                    else if (downArrow && selectedPrompt > -1) selectedPrompt--;
                    if (selectedPrompt != -1)
                    {
                        List<string> reversedPrompts = new List<string>(previousPrompts);
                        reversedPrompts.Reverse();
                        inputField.text = reversedPrompts[selectedPrompt];
                        inputField.caretPosition = inputField.text.Length;
                    }
                    else
                        inputField.text = null;
                }
                if (ChatCommandsPlugin.configTab.Value && InputManager.GetKeyDown(KeyCode.Tab) && closestCmd != null
                    && !inputField.text.StartsWith(closestCmd.name))
                {
                    inputField.text = closestCmd.name;
                    inputField.caretPosition = inputField.text.Length;
                }
            }
            plugins.ToList().ForEach(x => x.updateAction?.Invoke());
        }

        private void CreateUI()
        {
            if (isInitialized && assetBundle != null) return;
            isCreatingUI = true;
            selectedPrompt = -1;
            if (isInitialized)
            {
                Destroy(chatWindow);
                Destroy(textPrefab);
            }
            string location = Assembly.GetExecutingAssembly().Location;
            assetBundle = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(location), Path.GetFileNameWithoutExtension(location)));
            if (assetBundle == null)
            {
                ChatCommandsPlugin.logger.LogFatal("An error occured while creating the UI. " +
                    "(NOTE) 'Unable to read header from archive file' means you need to update the version of the asset bundle. Or else file was not found.");
                return;
            }
            GameObject chat = assetBundle.LoadAsset<GameObject>("Chat");
            GameObject text = assetBundle.LoadAsset<GameObject>("Text");
            Font font = assetBundle.LoadAsset<Font>("Font");
            if (chat != null && text != null && font != null)
            {
                chatWindow = Instantiate(chat);
                chatWindow.transform.SetParent(gameObject.transform);
                Transform window = chatWindow.transform.Find("Window");
                content = window.Find("Scroll").Find("Viewport").Find("Content");
                scrollRect = window.Find("Scroll").GetComponent<ScrollRect>();
                scrollRect.scrollSensitivity = ChatCommandsPlugin.configScroll.Value;
                canvas = chatWindow.GetComponent<Canvas>();
                canvas.enabled = false;
                inputField = window.Find("Input").GetComponent<TMP_InputField>();
                Action onSubmit = delegate
                {
                    if (!inputField.text.IsNullOrWhiteSpace())
                    {
                        string trim = inputField.text.Trim();
                        if (previousPrompts.Count == 0 || previousPrompts[previousPrompts.Count - 1] != trim)
                            previousPrompts.Add(trim);
                        string[] args = trim.Split(' ').Where(x => !x.IsNullOrWhiteSpace()).ToArray();
                        ChatCommand cmd = commands.FirstOrDefault(x => x.name == args[0]);
                        if (cmd != null) cmd.Execute(args.Skip(1).ToArray());
                        else Message($"Command called '{args[0]}' does not exist.");
                    }
                    selectedPrompt = -1;
                    inputField.text = null;
                    inputField.Select();
                    inputField.ActivateInputField();
                };
                inputField.onSubmit.AddListener(delegate { onSubmit.Invoke(); });
                inputField.onValueChanged.AddListener(delegate
                {
                    closestCmd = null;
                    autoComplete.text = null;
                    if (inputField.text.Length > 0 && inputField.text[0] == ' ')
                    {
                        inputField.text = inputField.text.Substring(1);
                        return;
                    }
                    if (!inputField.text.IsNullOrWhiteSpace())
                    {
                        int caretPosition = inputField.caretPosition == 0 ? inputField.caretPosition : inputField.caretPosition - 1;
                        string[] args = inputField.text.Split(' ').Where(x => !x.IsNullOrWhiteSpace()).ToArray();
                        List<ChatCommand> matches = commands.Where(x => x.name.StartsWith(args[0])).ToList();
                        if (matches.Count != 0)
                        {
                            matches.Sort((x, y) =>
                            {
                                string xName = x.name;
                                string yName = y.name;
                                int compare = xName.Length.CompareTo(yName.Length);
                                if (compare == 0)
                                    return string.Compare(xName, yName, StringComparison.Ordinal);
                                return compare;
                            });
                            closestCmd = matches.First();
                            if (closestCmd.name.Length != args[0].Length && inputField.text[caretPosition] == ' ')
                            {
                                closestCmd = null;
                                autoComplete.text = null;
                            }
                        }
                        if (closestCmd != null)
                        {
                            string autoCompleteText = closestCmd.name;
                            string exampleText = closestCmd.example;
                            if (exampleText != null)
                            {
                                if (inputField.text.Length >= autoCompleteText.Length)
                                {
                                    autoCompleteText = null;
                                    string[] exampleArgs = Regex.Split(exampleText,
                                        @"\s(?![^\[\]\{\}\(\)]*(?:\]|\}|\)))").Where(x => !x.IsNullOrWhiteSpace()).ToArray();
                                    exampleArgs = exampleArgs.Skip(args.Length - 1).ToArray();
                                    exampleText = inputField.text;
                                    for (int i = 0; i < exampleArgs.Length; i++)
                                        exampleText += $"{((i != 0 || inputField.text[caretPosition] != ' ') ? " " : "")}" +
                                        $"{exampleArgs[i]}";
                                }
                                else
                                    exampleText = $" {exampleText}";
                            }
                            autoComplete.text = autoCompleteText + exampleText;
                        }
                    }
                });
                autoComplete = inputField.gameObject.transform.Find("Text Area").Find("Autocomplete").GetComponent<TMP_Text>();
                TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(font);
                Button enter = window.Find("Enter").GetComponent<Button>();
                enter.onClick.AddListener(delegate { onSubmit.Invoke(); });
                enter.gameObject.transform.Find("Text").GetComponent<TextMeshProUGUI>().font = fontAsset;
                Button bottom = window.Find("Bottom").GetComponent<Button>();
                bottom.onClick.AddListener(delegate { ScrollToBottom(); });
                bottom.gameObject.transform.Find("Text").GetComponent<TextMeshProUGUI>().font = fontAsset;
                Button top = window.Find("Top").GetComponent<Button>();
                top.onClick.AddListener(delegate
                {
                    Canvas.ForceUpdateCanvases();
                    scrollRect.verticalNormalizedPosition = 1f;
                });
                top.gameObject.transform.Find("Text").GetComponent<TextMeshProUGUI>().font = fontAsset;
                TransformLoop(window, (Transform current) =>
                {
                    TextMeshProUGUI textMesh = current.GetComponent<TextMeshProUGUI>();
                    if (textMesh) textMesh.font = fontAsset;
                });
                textPrefab = Instantiate(text);
                textPrefab.GetComponent<TextMeshProUGUI>().font = fontAsset;
                textPrefab.transform.SetParent(gameObject.transform);
                if (!isInitialized)
                {
                    isInitialized = true;
                    ChatCommandsPlugin.logger.LogMessage($"{ChatCommandsPlugin.modName} has been initialized.".ToString());
                    if (onInit != null)
                    {
                        foreach (var method in onInit.GetInvocationList())
                        {
                            (ChatPlugin plugin, ChatCommand[] customCommands) = ((ChatPlugin, ChatCommand[]))method.DynamicInvoke();
                            if (!Chainloader.PluginInfos.Values.Any(x => x.Metadata.GUID == plugin.guid)) continue;
                            if (plugins.Any(x => x.guid == plugin.guid)) continue;
                            if (customCommands.Length == 0) continue;
                            plugins.Add(plugin);
                            foreach (ChatCommand cmd in customCommands)
                            {
                                cmd.plugin = plugin;
                                int count = commands.Count(x => x.name == cmd.name);
                                if (count > 0)
                                {
                                    string oldName = cmd.name;
                                    cmd.name = $"{cmd.name}_{count}";
                                    Message($"Duplicate found with name \"{oldName}\" from plugin \"{plugin.guid}\" ({cmd.name})");
                                }
                                commands.Add(cmd);
                            }
                        }
                    }
                }
                else
                {
                    ChatMessage[] tempMessages = previousTexts.ToArray();
                    previousTexts.Clear();
                    tempMessages.ToList().ForEach(x => Message(x.text, x.name, x.color, x.size, x.time));
                    Message("Reloaded asset bundle as it had unexpectedly unloaded.");
                }
                isCreatingUI = false;
            }
            else ChatCommandsPlugin.logger.LogFatal("Failed to load essential GameObjects from the asset bundle.");
        }

        public void Message(object text, string name = null, Color color = default, int size = -1, DateTime time = default)
        {
            ChatMessage message = new ChatMessage(text, name, color, size, time);
            previousTexts.Add(message);
            GameObject newText = Instantiate(textPrefab);
            newText.GetComponent<TMP_Text>().text = $"/// {message.time:dd/MM/yyyy hh:mm:ss tt} [{message.name}]:" +
                $" {ChatUtility.Color(message.color)}{message.text}";
            newText.GetComponent<TMP_Text>().fontSize = message.size;
            newText.transform.SetParent(content);
            ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            Canvas.ForceUpdateCanvases();
            scrollRect.verticalNormalizedPosition = 0f;
        }

        private void TransformLoop(Transform transform, Action<Transform> action)
        {
            foreach (Transform current in transform)
            {
                action.Invoke(current);
                TransformLoop(current, action);
            }
        }
    }
}

[BepInPlugin(modGUID, modName, modVer)]
public class ChatCommandsPlugin : BaseUnityPlugin
{
    internal const string modGUID = "BULLETBOT.ChatCommands";
    internal const string modName = "Chat Commands";
    private const string modVer = "2.3.9";

    public static ManualLogSource logger;

    public static ConfigEntry<float> configScroll;
    public static ConfigEntry<int> configSize;
    public static ConfigEntry<bool> configTab;
    public static ConfigEntry<bool> configModifierEnabled;
    public static ConfigEntry<KeyCode> configModifier;
    public static ConfigEntry<KeyCode> configToggle;

    void Awake()
    {
        logger = BepInEx.Logging.Logger.CreateLogSource(modName);
        configScroll = Config.Bind("General", "Scrolling Sensitivity", 40f);
        configSize = Config.Bind("General", "Font Size", 32);
        configTab = Config.Bind("Keybinds", "Tab Enabled", true);
        configModifierEnabled = Config.Bind("Keybinds", "Modifier Enabled", true);
        configModifier = Config.Bind("Keybinds", "Modifier Button", KeyCode.LeftControl);
        configToggle = Config.Bind("Keybinds", "Toggle Button", KeyCode.Q);
        Universe.Init(delegate
        {
            GameObject manager = new GameObject();
            DontDestroyOnLoad(manager);
            manager.name = "ChatCommandsManager";
            ChatManager chat = manager.AddComponent<ChatManager>();
        });
    }
}