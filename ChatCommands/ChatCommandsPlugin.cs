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
        public string mod;
        private Action<ChatCommand, string[]> action;

        public ChatCommand(string name, string description, string example, Action<ChatCommand, string[]> action)
        {
            this.name = name;
            this.description = description;
            this.example = example;
            this.action = action;
        }

        public void Message(object text, Color color = default, int size = -1)
        {
            string newName = "";
            string[] splitName = name.Split('_');
            for (int i = 0; i < splitName.Length; i++)
            {
                string subName = splitName[i];
                newName += subName.ToUpper().Substring(0, 1) + subName.ToLower().Substring(1, subName.Length - 1) + (i != (splitName.Length - 1) ? " " : null);
            }
            ChatManager.instance.Message(text, newName, color, size);
        }

        public void Execute(string[] args)
        {
            try
            {
                action.Invoke(this, args);
            }
            catch (Exception e)
            {
                Message(e.ToString(), new Color(1, 0, 0));
            }
        }
    }

    public class ChatManager : MonoBehaviour
    {
        public static ChatManager instance;
        public delegate (string, ChatCommand[]) onInitHandler();
        public static event onInitHandler onInit;
        private bool isInitialized = false;
        private bool isCreatingUI = false;
        private AssetBundle assetBundle;

        private TMP_InputField inputField;
        private Canvas canvas;
        private Transform content;
        private GameObject chatWindow;
        private ScrollRect scrollRect;

        private List<string> previousPrompts = new List<string>();
        private int selectedPrompt;

        private List<ChatMessage> previousTexts = new List<ChatMessage>();
        private GameObject textPrefab;

        private GameObject tempSelected;
        private CursorLockMode tempCursor;

        private List<string> mods = new List<string>();
        private List<ChatCommand> commands = new List<ChatCommand>();

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
                    if (selectedPrompt == -1)
                    {
                        inputField.text = null;
                        return;
                    }
                    List<string> reversedPrompts = new List<string>(previousPrompts);
                    reversedPrompts.Reverse();
                    inputField.text = reversedPrompts[selectedPrompt];
                    inputField.caretPosition = inputField.text.Length;
                }
            }
        }

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
                    "Shows list of commands and can look at other commands.",
                    "?name(string)",
                    delegate (ChatCommand _this, string[] args)
                    {
                        StringBuilder sb = new StringBuilder();
                        if (args.Length >= 1)
                        {
                            ChatCommand cmd = commands.FirstOrDefault(x => x.name == args[0]);
                            if (cmd != null)
                            {
                                sb.Append($"\nName: {cmd.name}\nDescription: {cmd.description}");
                                if (cmd.mod != null) sb.Append($"\nMod: {cmd.mod}");
                                sb.Append($"\nExample: {cmd.name}");
                                if (cmd.example != null)
                                {
                                    sb.Append($" {cmd.example}");
                                    if (cmd.example.Contains("?")) sb.Append($"\n{ChatUtility.Color(Color.yellow)}? means optional.");
                                    if (cmd.example.Contains("#")) sb.Append($"\n{ChatUtility.Color(Color.yellow)}# means arguments are connected.");
                                    if (cmd.example.Contains("{") && cmd.example.Contains("}"))
                                        sb.Append($"\n{ChatUtility.Color(Color.yellow)}{{}} means comment.");
                                    if (cmd.example.Contains("[") && cmd.example.Contains("]"))
                                        sb.Append($"\n{ChatUtility.Color(Color.yellow)}[] means how the argument should be.");
                                }
                            }
                            else sb.Append($"Command called '{args[0]}' does not exist.");
                        }
                        else
                        {
                            sb.Append($"\nCommands ({commands.Count}):\n");
                            foreach (ChatCommand cmd in commands) sb.Append($"\n{cmd.name}");
                        }
                        _this.Message(sb.ToString());
                    }),
                new ChatCommand("clear",
                    "Clears the chat.",
                    null,
                    delegate (ChatCommand _this, string[] _)
                    {
                        previousTexts.Clear();
                        foreach (Transform message in content) Destroy(message.gameObject);
                        _this.Message("Chat has been cleared!");
                    }),
                new ChatCommand("mods",
                    "Shows a list of mods.",
                    null,
                    delegate (ChatCommand _this, string[] _)
                    {
                        StringBuilder sb = new StringBuilder();
                        BepInPlugin[] pluginInfos = Chainloader.PluginInfos.Values.Select(x => x.Metadata).ToArray();
                        sb.Append($"\nCount: {pluginInfos.Length}\n");
                        for (int i = 0; i < pluginInfos.Length; i++)
                        {
                            BepInPlugin pluginInfo = pluginInfos[i];
                            sb.Append($"\nName: {pluginInfo.Name}\nGUID: {pluginInfo.GUID}\nVersion: {pluginInfo.Version}{((i + 1) != commands.Count ? "\n" : null)}");
                        }
                        _this.Message(sb.ToString());
                    })
            });
            CreateUI();
            SceneManager.activeSceneChanged += delegate { CreateUI(); };
        }

        private void TransformLoop(Transform transform, Action<Transform> action)
        {
            foreach (Transform current in transform)
            {
                action.Invoke(current);
                TransformLoop(current, action);
            }
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
                ChatCommandsPlugin.logger.LogFatal("An error occured while initializing. " +
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
                        if (previousPrompts.Count == 0 || previousPrompts[previousPrompts.Count - 1] != inputField.text)
                            previousPrompts.Add(inputField.text);
                        string[] args = inputField.text.Trim().Split(' ').Where(x => !x.IsNullOrWhiteSpace()).ToArray();
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
                window.Find("Button").GetComponent<Button>().onClick.AddListener(delegate { onSubmit.Invoke(); });
                TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(font);
                TransformLoop(window, (Transform current) =>
                {
                    TextMeshProUGUI tmp = current.GetComponent<TextMeshProUGUI>();
                    if (tmp) tmp.font = fontAsset;
                });
                textPrefab = Instantiate(text);
                textPrefab.GetComponent<TextMeshProUGUI>().font = fontAsset;
                textPrefab.transform.SetParent(gameObject.transform);
                if (!isInitialized)
                {
                    ChatCommandsPlugin.logger.LogMessage($"{ChatCommandsPlugin.modName} has been initialized.".ToString());
                    if (onInit != null)
                    {
                        foreach (Delegate del in onInit.GetInvocationList())
                        {
                            (string, ChatCommand[]) args = ((string, ChatCommand[]))del.DynamicInvoke();
                            if (instance.mods.Contains(args.Item1)) continue;
                            instance.mods.Add(args.Item1);
                            foreach (ChatCommand cmd in args.Item2)
                            {
                                cmd.mod = args.Item1;
                                instance.commands.Add(cmd);
                            }
                        }
                    }
                    isInitialized = true;
                }
                else
                {
                    ChatMessage[] tempMessages = previousTexts.ToArray();
                    previousTexts.Clear();
                    foreach (ChatMessage message in tempMessages) Message(message.text, message.name, message.color, message.size, message.time);
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
            newText.GetComponent<TMP_Text>().text = $"/// {message.time.ToString("dd/MM/yyyy hh:mm tt")} [{message.name}]:" +
                $" {ChatUtility.Color(message.color)}{message.text}";
            newText.GetComponent<TMP_Text>().fontSize = message.size;
            newText.transform.SetParent(content);
            ScrollToBottom();
        }

        public void ScrollToBottom()
        {
            Canvas.ForceUpdateCanvases();
            scrollRect.verticalNormalizedPosition = 0f;
        }
    }
}

[BepInPlugin(modGUID, modName, modVer)]
public class ChatCommandsPlugin : BaseUnityPlugin
{
    internal const string modGUID = "BULLETBOT.ChatCommands";
    internal const string modName = "Chat Commands";
    private const string modVer = "1.0.0";

    public static ManualLogSource logger;

    public static ConfigEntry<float> configScroll;
    public static ConfigEntry<int> configSize;
    public static ConfigEntry<bool> configModifierEnabled;
    public static ConfigEntry<KeyCode> configModifier;
    public static ConfigEntry<KeyCode> configToggle;

    void Awake()
    {
        logger = BepInEx.Logging.Logger.CreateLogSource(modName);
        configScroll = Config.Bind("General", "Scrolling Sensitivity", 40f);
        configSize = Config.Bind("General", "Font Size", 32);
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