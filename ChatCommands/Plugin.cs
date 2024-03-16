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
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.Input;

namespace ChatCommands
{
    public static class ChatUtility
    {
        public static string Color(Color color) => $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>";
    }

    public class ChatCommand
    {
        public string name;
        public string description;
        public string example;
        public string mod;
        private Action<ChatCommand, string[]> method;

        public ChatCommand(string name, string description, string example, Action<ChatCommand, string[]> method)
        {
            this.name = name;
            this.description = description;
            this.example = example;
            this.method = method;
        }

        public void Message(object text, Color color = default, int size = default)
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
                method.Invoke(this, args.Skip(1).ToArray());
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

        private TMP_InputField inputField;
        private Canvas canvas;
        private Transform content;
        private ScrollRect scrollRect;
        private GameObject tempSelected;
        private CursorLockMode tempState;

        private List<Action<bool>> canvasActions = new List<Action<bool>>();

        private List<string> previousPrompts = new List<string>();
        private int selectedPrompt = -1;

        private GameObject textPrefab;
        private int fontSize = 32;

        private List<string> mods = new List<string>();
        private List<ChatCommand> commands = new List<ChatCommand>();

        void Update()
        {
            if (!isInitialized) return;
            if (Plugin.configModifierEnabled.Value
                ?
                (InputManager.GetKey(Plugin.configModifier.Value) && InputManager.GetKeyDown(Plugin.configToggle.Value))
                :
                InputManager.GetKeyDown(Plugin.configToggle.Value)
                )
            {
                foreach (Action<bool> action in canvasActions) action.Invoke(canvas.enabled);
                canvas.enabled = !canvas.enabled;
                if (canvas.enabled)
                {
                    selectedPrompt = -1;
                    inputField.text = null;
                    tempSelected = EventSystem.current.currentSelectedGameObject;
                    tempState = Cursor.lockState;
                    inputField.Select();
                    ScrollToBottom();
                }
                else
                {
                    EventSystem.current.SetSelectedGameObject(tempSelected);
                    tempSelected = null;
                    Cursor.lockState = tempState;
                    tempState = default;
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
        }

        private void Initialize()
        {
            if (isInitialized) return;
            AssetBundle assetBundle = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "assetbundle"));
            if (assetBundle == null)
            {
                Plugin.logger.LogFatal("An error occured while initializing. " +
                    "(NOTE) 'Unable to read header from archive file' means you need to update the version of the asset bundle. Or missing file.");
                return;
            }
            GameObject chat = assetBundle.LoadAsset<GameObject>("Chat");
            GameObject text = assetBundle.LoadAsset<GameObject>("Text");
            if (chat != null && text != null)
            {
                GameObject chatObject = Instantiate(chat);
                chatObject.transform.SetParent(gameObject.transform);
                Transform window = chatObject.transform.Find("Window");
                content = window.Find("Scroll").Find("Viewport").Find("Content");
                scrollRect = window.Find("Scroll").GetComponent<ScrollRect>();
                scrollRect.scrollSensitivity = Plugin.configScroll.Value;
                canvas = chatObject.GetComponent<Canvas>();
                canvas.enabled = false;
                inputField = window.Find("Input").GetComponent<TMP_InputField>();
                inputField.onSubmit.AddListener(delegate
                {
                    if (!inputField.text.IsNullOrWhiteSpace())
                    {
                        previousPrompts.Add(inputField.text);
                        string[] args = inputField.text.Trim().Split(' ').Where(x => !x.IsNullOrWhiteSpace()).ToArray();
                        ChatCommand cmd = commands.FirstOrDefault(x => x.name == args[0]);
                        if (cmd != null) cmd.Execute(args);
                        else Message($"Command called '{args[0]}' does not exist.");
                    }
                    selectedPrompt = -1;
                    inputField.text = null;
                    inputField.Select();
                    inputField.ActivateInputField();
                });
                textPrefab = Instantiate(text);
                textPrefab.transform.SetParent(gameObject.transform);
                isInitialized = true;
                Plugin.logger.LogMessage($"{Plugin.modName} has been initialized.".ToString());
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
            else Plugin.logger.LogFatal("Failed to load essential GameObjects from the asset bundle.");
        }

        public void Message(object text, string name = null, Color color = default, int size = default)
        {
            if (color == default) color = new Color(1, 1, 1);
            if (size == default) size = fontSize;
            DateTime dateTime = DateTime.Now;
            GameObject newText = Instantiate(textPrefab);
            newText.GetComponent<TMP_Text>().text = $"{dateTime.ToLongTimeString()} {dateTime.ToShortDateString()} [{(name != null ? name : "System")}]:" +
                $" {ChatUtility.Color(color)}{text}";
            newText.GetComponent<TMP_Text>().fontSize = size;
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
public class Plugin : BaseUnityPlugin
{
    internal const string modGUID = "BULLETBOT.ChatCommandsMono";
    internal const string modName = "Chat Commands (Mono)";
    private const string modVer = "1.0.0";

    public static ManualLogSource logger;

    public static ConfigEntry<float> configScroll;
    public static ConfigEntry<bool> configModifierEnabled;
    public static ConfigEntry<KeyCode> configModifier;
    public static ConfigEntry<KeyCode> configToggle;

    void Awake()
    {
        logger = BepInEx.Logging.Logger.CreateLogSource(modName);
        configScroll = Config.Bind("General", "Scrolling Sensitivity", 40f);
        configModifierEnabled = Config.Bind("Keybinds", "Modifier Enabled", true);
        configModifier = Config.Bind("Keybinds", "Modifier Keybind", KeyCode.LeftControl);
        configToggle = Config.Bind("Keybinds", "Toggle Keybind", KeyCode.Q);
        Universe.Init(delegate
        {
            GameObject manager = new GameObject();
            manager.name = "ChatCommandsManager";
            manager.AddComponent<ChatManager>();
            DontDestroyOnLoad(manager);
            ChatManager.instance.Initialize();
        });
    }
}