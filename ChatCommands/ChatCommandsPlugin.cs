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
        public static string ToUndercase(string name) => name.Replace(' ', '_').ToLower();
        public static string ToStrings(this ChatParameter[] parameters) => parameters != null && parameters.Length != 0
            ? string.Join(" ", parameters.Select(x => x.ToString())) : null;
    }

    public class ChatPlugin
    {
        public readonly string guid;
        public readonly ChatCommand[] commands;
        public readonly Action updateAction;
        public readonly object[] parsers;

        public ChatPlugin(string guid, ChatCommand[] commands, object[] parsers = null)
        {
            this.guid = guid;
            this.commands = commands;
            this.parsers = parsers;
        }

        public ChatPlugin(string guid, ChatCommand[] commands, Action updateAction, object[] parsers = null)
        {
            this.guid = guid;
            this.commands = commands;
            this.updateAction = updateAction;
            this.parsers = parsers;
        }
    }

    public class ChatMessage
    {
        public readonly object text;
        public readonly string name;
        public readonly Color color;
        public readonly int size;
        public readonly DateTime time;

        public ChatMessage(object text, string name = null, Color color = default, int size = -1, DateTime time = default)
        {
            this.text = text;
            this.name = name.IsNullOrWhiteSpace() ? "System" : name;
            this.color = color == default ? new Color(1, 1, 1) : color;
            this.size = size == -1 ? ChatCommandsPlugin.configSize.Value : size;
            this.time = time == default ? DateTime.Now : time;
        }
    }

    public class ChatParameter
    {
        protected string name;
        protected string description;
        public Type type { get; protected set; }
        public bool optional { get; protected set; }

        public ChatParameter(string name, Type type, bool optional = false)
        {
            this.name = ChatUtility.ToUndercase(name);
            this.type = type;
            this.optional = optional;
        }

        public ChatParameter(string name, string description, Type type, bool optional = false)
        {
            this.name = ChatUtility.ToUndercase(name);
            this.description = description;
            this.type = type;
            this.optional = optional;
        }

        public override string ToString() => $"{(optional ? "?" : null)}{name}[{type.Name}]{(description != null ? $"({description})" : null)}";
    }

    public class ChatLinkedParameter : ChatParameter
    {
        public ChatLinkedParameter(string name, Type type, bool optional = false) : base(name, type, optional)
        {
            this.name = ChatUtility.ToUndercase(name);
            this.type = type;
            this.optional = optional;
        }

        public ChatLinkedParameter(string name, string description, Type type, bool optional = false) : base(name, description, type, optional)
        {
            this.name = ChatUtility.ToUndercase(name);
            this.description = description;
            this.type = type;
            this.optional = optional;
        }

        public override string ToString() => $"#{base.ToString()}";
    }

    public class ChatArguments
    {
        private ChatArgument[] arguments;

        public ChatArguments(ChatArgument[] arguments) => this.arguments = arguments;

        public bool TryGetArgument(int index, out ChatArgument argument)
        {
            if (index >= 0 && index < arguments.Length)
            {
                argument = arguments[index];
                return true;
            }
            else
            {
                argument = null;
                return false;
            }
        }
    }

    public class ChatArgumentException : Exception
    {
        public readonly Color color;

        public ChatArgumentException(string message, Color color = default) : base(message) => this.color = color == default ? new Color(1, 1, 1) : color;
    }

    public class ChatArgumentParser<T>
    {
        private Func<int, string, T> parse;
        public readonly string example;
        public readonly string plugin;

        public ChatArgumentParser(Func<int, string, T> parse, string example)
        {
            this.parse = parse;
            this.example = example;
        }

        public T Parse(int index, string text) => parse(index, text);
    }

    public class ChatArgument
    {
        private int index;
        private Type type;
        private string value;
        private string[] values;

        public ChatArgument(int index, Type type, string value)
        {
            this.index = index;
            this.type = type;
            this.value = value;
        }

        public ChatArgument(int index, Type type, string[] values)
        {
            this.index = index;
            this.type = type;
            this.values = values;
        }

        private T ParseValue<T>(int index, string value)
        {
            if (type != typeof(T))
            {
                throw new ChatArgumentException(
                    $"The argument {index} has a type of \"{type.Name}\" instead of the expected type \"{typeof(T).Name}\"", Color.yellow);
            }
            T ret;
            try
            {
                ret = (T)Convert.ChangeType(value, type);
            }
            catch (InvalidCastException)
            {
                Dictionary<Type, object> parsers = (Dictionary<Type, object>)ChatCommandsManager.instance.GetType().GetField("parsers",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ChatCommandsManager.instance);
                if (parsers.TryGetValue(typeof(T), out object parser))
                    ret = ((ChatArgumentParser<T>)parser).Parse(index, value);
                else
                    throw new ChatArgumentException($"No parser has been found for type \"{typeof(T).Name}\"", Color.red);
            }
            catch (Exception ex)
            {
                throw new ChatArgumentException($"Error parsing argument {index}: {ex.Message}", Color.red);
            }
            return ret;
        }

        public T GetValue<T>()
        {
            if (values != null)
                throw new ChatArgumentException("Use GetValues<T>() instead", Color.yellow);
            return ParseValue<T>(index, value);
        }

        public T[] GetValues<T>()
        {
            if (value != null)
                throw new ChatArgumentException("Use GetValue<T>() instead", Color.yellow);
            T[] ret = new T[values.Length];
            for (int i = 0; i < values.Length; i++)
                ret[i] = ParseValue<T>(index + i, values[i]);
            return ret;
        }
    }

    public class ChatCommand
    {
        public readonly string name;
        public readonly string description;
        public readonly ChatParameter[] parameters;
        private readonly Action<string> executeAction1;
        private Action<string, ChatArguments> executeAction2;
        public readonly string plugin;

        public ChatCommand(string name, Action<string> executeAction1)
        {
            this.name = ChatUtility.ToUndercase(name);
            this.executeAction1 = executeAction1;
        }

        public ChatCommand(string name, string description, Action<string> executeAction1)
        {
            this.name = ChatUtility.ToUndercase(name);
            this.description = description;
            this.executeAction1 = executeAction1;
        }

        public ChatCommand(string name, ChatParameter[] parameters, Action<string, ChatArguments> executeAction2)
        {
            this.name = ChatUtility.ToUndercase(name);
            this.parameters = parameters;
            this.executeAction2 = executeAction2;
        }

        public ChatCommand(string name, string description, ChatParameter[] parameters, Action<string, ChatArguments> executeAction2)
        {
            this.name = ChatUtility.ToUndercase(name);
            this.description = description;
            this.parameters = parameters;
            this.executeAction2 = executeAction2;
        }

        private bool IsValid()
        {
            if (parameters == null) return true;
            if (parameters.Length == 0) return false;
            int linkedCount = parameters.Count(x => x.GetType() == typeof(ChatLinkedParameter));
            if (linkedCount >= 2 || (linkedCount == 1 && parameters.Last().GetType() != typeof(ChatLinkedParameter)))
                return false;
            bool isOptionalFound = false;
            for (int i = 0; i < parameters.Length; i++)
            {
                ChatParameter parameter = parameters[i];
                if (parameter.optional)
                    isOptionalFound = true;
                else if (isOptionalFound && !parameter.optional)
                    return false;
            }
            return true;
        }

        public void Execute(string[] args)
        {
            if (!IsValid())
            {
                ChatCommandsManager.instance.Message($"Command parameters are invalid", ChatUtility.Simplify(name), Color.red);
                return;
            }
            try
            {
                executeAction1?.Invoke(name);
                if (executeAction2 != null)
                {
                    List<ChatArgument> actualArgs = new List<ChatArgument>();
                    int parameterCount = parameters.Count(x => !x.optional);
                    if (parameterCount != 0 && parameterCount > args.Length)
                    {
                        ChatCommandsManager.instance.Message($"Requires at least {parameterCount} argument(s)", ChatUtility.Simplify(name));
                        return;
                    }
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (args.Length > i)
                        {
                            ChatParameter parameter = parameters[i];
                            if (parameter.GetType() == typeof(ChatLinkedParameter))
                            {
                                List<string> values = new List<string>();
                                foreach (string str in args.Skip(i))
                                    values.Add(str);
                                actualArgs.Add(new ChatArgument(i + 1, parameter.type, values.ToArray()));
                            }
                            else
                                actualArgs.Add(new ChatArgument(i + 1, parameter.type, args[i]));
                        }
                    }
                    executeAction2.Invoke(name, new ChatArguments(actualArgs.ToArray()));
                }
            }
            catch (ChatArgumentException ex)
            {
                ChatCommandsManager.instance.Message(ex.Message, ChatUtility.Simplify(name), ex.color);
            }
            catch (Exception ex)
            {
                ChatCommandsManager.instance.Message(ex.ToString(), ChatUtility.Simplify(name), Color.red);
            }
        }
    }

    public class ChatCommandsManager : MonoBehaviour
    {
        public static ChatCommandsManager instance;
        public delegate ChatPlugin onInitHandler();
        public static event onInitHandler onInit;
        private AssetBundle assetBundle;
        private bool isInitialized = false;
        private bool isCreatingUI = false;

        private List<ChatPlugin> plugins = new List<ChatPlugin>();
        private List<ChatCommand> commands = new List<ChatCommand>();
        private Dictionary<string, string[]> duplicateCommands = new Dictionary<string, string[]>();
        private Dictionary<Type, object> parsers = new Dictionary<Type, object>();

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
            new ChatCommand[]
            {
                new ChatCommand("clear",
                    "Clears all the messages",
                    name =>
                    {
                        previousTexts.Clear();
                        foreach (Transform message in content) Destroy(message.gameObject);
                        Message("Messages has been cleared!", ChatUtility.Simplify(name));
                    }),
                new ChatCommand("help",
                    "Shows a list of commands",
                    new ChatParameter[] { new ChatParameter("command", typeof(string), true) },
                    (name, args) =>
                    {
                        StringBuilder sb = new StringBuilder();
                        if (args.TryGetArgument(0, out ChatArgument argument))
                        {
                            string text = argument.GetValue<string>();
                            ChatCommand cmd = commands.FirstOrDefault(x => x.name == text);
                            if (cmd != null)
                            {
                                sb.Append($"\nName: {cmd.name}");
                                if (cmd.description != null) sb.Append($"\nDescription: {cmd.description}");
                                if (cmd.plugin != null) sb.Append($"\nPlugin: {cmd.plugin}");
                                if (cmd.parameters != null)
                                {
                                    string parameters = cmd.parameters.ToStrings();
                                    sb.Append($"\nParameters: {parameters}");
                                    if (parameters.Contains("?")) sb.Append($"\n{ChatUtility.Color(Color.yellow)}? means optional");
                                    if (parameters.Contains("#")) sb.Append($"\n{ChatUtility.Color(Color.yellow)}# means the following arguments are linked");
                                    if (parameters.Contains("(") && parameters.Contains(")"))
                                        sb.Append($"\n{ChatUtility.Color(Color.yellow)}{{}} shows a description");
                                    if (parameters.Contains("[") && parameters.Contains("]"))
                                        sb.Append($"\n{ChatUtility.Color(Color.yellow)}[] shows what type it is");
                                }
                            }
                            else
                                sb.Append($"Command called \"{text}\" does not exist");
                        }
                        else
                        {
                            sb.Append($"\nCommands ({commands.Count}):\n");
                            commands.ForEach(x => sb.Append($"\n{x.name}"));
                        }
                        Message(sb.ToString(), ChatUtility.Simplify(name));
                    }),
                new ChatCommand("parsers",
                    "Shows a list of parsers",
                    new ChatParameter[] { new ChatParameter("type", typeof(string), true), new ChatParameter("input", typeof(string), true) },
                    (name, args) =>
                    {
                        StringBuilder sb = new StringBuilder();
                        if (args.TryGetArgument(0, out ChatArgument arg0))
                        {
                            string text = arg0.GetValue<string>();
                            Type type = parsers.Select(x => x.Key).FirstOrDefault(x => x.Name == text);
                            if (type != null && parsers.TryGetValue(type, out object parser))
                            {
                                if (args.TryGetArgument(1, out ChatArgument arg1))
                                {
                                    string exceptionMessage = null;
                                    try
                                    {
                                        parser.GetType().GetMethod("Parse").Invoke(parser, new object[] { 2, arg1.GetValue<string>() });
                                    }
                                    catch (TargetInvocationException ex)
                                    {
                                        Exception innerEx = ex.InnerException;
                                        if (innerEx.GetType() == typeof(ChatArgumentException))
                                            exceptionMessage = innerEx.Message;
                                        else
                                            exceptionMessage = "Something went wrong";
                                    }
                                    sb.Append(exceptionMessage != null ? exceptionMessage : "Valid");
                                }
                                else
                                {
                                    sb.Append($"\nName: {type.Name}" +
                                    $"\nExample: {parser.GetType().GetField("example").GetValue(parser)}");
                                    string plugin = (string)parser.GetType().GetField("plugin").GetValue(parser);
                                    if (plugin != null) sb.Append($"\nPlugin: {plugin}");
                                }
                            }
                            else
                                sb.Append($"Parser with type \"{text}\" does not exist");
                        }
                        else
                        {
                            if (parsers.Count == 0)
                                sb.Append("No parsers has been found");
                            else
                            {
                                sb.Append($"\nParsers ({parsers.Count}):\n");
                                parsers.ToList().ForEach(x => sb.Append($"\n{x.Key.Name}"));
                            }
                        }
                        Message(sb.ToString(), ChatUtility.Simplify(name));
                    }),
                new ChatCommand("plugins",
                    "Shows a list of plugins",
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
                                $"Dependency: {(plugins.Any(x => x.guid == pluginInfo.GUID) ? "Yes" : "No")}" +
                                $"{(i != (pluginInfos.Length - 1) ? "\n" : null)}");
                        }
                        Message(sb.ToString(), ChatUtility.Simplify(name));
                    })
            }.ToList().ForEach(x =>
            {
                DuplicateCheck(x);
                commands.Add(x);
            });
            Create();
            SceneManager.activeSceneChanged += delegate { Create(); };
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

        private void Create()
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
                    "(NOTE) 'Unable to read header from archive file' means you need to update the version of the asset bundle. Or else file was not found");
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
                        else Message($"Command called \"{args[0]}\" does not exist");
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
                            string name = closestCmd.name;
                            string parameters = closestCmd.parameters.ToStrings();
                            if (parameters != null)
                            {
                                if (inputField.text.Length >= name.Length)
                                {
                                    name = null;
                                    string[] splitParameters = Regex.Split(parameters,
                                        @"\s(?![^\[\]\(\)]*(?:\]|\)))").Where(x => !x.IsNullOrWhiteSpace()).ToArray();
                                    string lastParameter = splitParameters[splitParameters.Length - 1];
                                    splitParameters = splitParameters.Skip(args.Length - 1).ToArray();
                                    parameters = inputField.text;
                                    if (splitParameters.Length != 0)
                                    {
                                        for (int i = 0; i < splitParameters.Length; i++)
                                            parameters += $"{((i != 0 || inputField.text[caretPosition] != ' ') ? " " : "")}{splitParameters[i]}";
                                    }
                                    else if (closestCmd.parameters.Last().GetType() == typeof(ChatLinkedParameter))
                                        parameters += $"{(inputField.text[caretPosition] != ' ' ? " " : "")}{lastParameter}";
                                }
                                else
                                    parameters = $" {parameters}";
                            }
                            autoComplete.text = name + parameters;
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
                IterateTransform(window, (Transform current) =>
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
                    ChatCommandsPlugin.logger.LogMessage($"{ChatCommandsPlugin.modName} has been initialized".ToString());
                    if (onInit != null)
                    {
                        foreach (var method in onInit.GetInvocationList())
                        {
                            ChatPlugin plugin = (ChatPlugin)method.DynamicInvoke();
                            bool shouldContinue = false;
                            if (!Chainloader.PluginInfos.Values.Any(x => x.Metadata.GUID == plugin.guid)) shouldContinue = true;
                            if (plugins.Any(x => x.guid == plugin.guid)) shouldContinue = true;
                            if (plugin.commands.Length == 0) shouldContinue = true;
                            if (plugin.parsers != null && plugin.parsers.Length != 0)
                            {
                                foreach (object parser in plugin.parsers)
                                {
                                    Type type = parser.GetType();
                                    if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ChatArgumentParser<>)
                                        || type.GetGenericArguments().Length != 1)
                                    {
                                        shouldContinue = true;
                                    }
                                }
                            }
                            if (shouldContinue) continue;
                            foreach (ChatCommand cmd in plugin.commands)
                            {
                                DuplicateCheck(cmd);
                                cmd.GetType().GetField("plugin").SetValue(cmd, plugin.guid);
                                commands.Add(cmd);
                            }
                            plugin.GetType().GetField("commands").SetValue(plugin, null);
                            if (plugin.parsers != null && plugin.parsers.Length != 0)
                            {
                                foreach (object parser in plugin.parsers)
                                {
                                    Type genericType = parser.GetType().GetGenericArguments()[0];
                                    if (parsers.ContainsKey(genericType)) continue;
                                    object instance = Activator.CreateInstance(typeof(ChatArgumentParser<>).MakeGenericType(genericType),
                                        parser.GetType().GetField("parse", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(parser),
                                        parser.GetType().GetField("example").GetValue(parser));
                                    instance.GetType().GetField("plugin").SetValue(instance, plugin.guid);
                                    parsers.Add(genericType, instance);
                                }
                            }
                            plugin.GetType().GetField("parsers").SetValue(plugin, null);
                            plugins.Add(plugin);
                        }
                    }
                }
                else
                {
                    ChatMessage[] tempMessages = previousTexts.ToArray();
                    previousTexts.Clear();
                    tempMessages.ToList().ForEach(x => Message(x.text, x.name, x.color, x.size, x.time));
                    Message("Reloaded asset bundle as it had unexpectedly unloaded", null, Color.yellow);
                }
                isCreatingUI = false;
            }
            else
                ChatCommandsPlugin.logger.LogFatal("Failed to load essential GameObjects from the asset bundle");
        }

        private void DuplicateCheck(ChatCommand cmd)
        {
            if (!instance.commands.Any(x => x.name == cmd.name)) return;
            string currentName = cmd.name;
            foreach (KeyValuePair<string, string[]> pair in duplicateCommands)
            {
                foreach (string name in pair.Value)
                {
                    if (currentName == name)
                    {
                        currentName = pair.Key;
                        break;
                    }
                }
            }
            int index = 1;
            string newName;
            if (duplicateCommands.TryGetValue(currentName, out string[] commands))
            {
                index = commands.Length + 1;
                newName = string.Format("{0}_{1}", currentName, index);
                duplicateCommands[cmd.name] = new List<string>(commands)
                {
                    string.Format("{0}_{1}", currentName, index)
                }.ToArray();
            }
            else
            {
                newName = string.Format("{0}_{1}", currentName, index);
                duplicateCommands.Add(cmd.name, new string[] { newName });
            }
            cmd.GetType().GetField("name").SetValue(cmd, newName);
        }

        private void ScrollToBottom()
        {
            Canvas.ForceUpdateCanvases();
            scrollRect.verticalNormalizedPosition = 0f;
        }

        private void IterateTransform(Transform transform, Action<Transform> action)
        {
            foreach (Transform current in transform)
            {
                action.Invoke(current);
                IterateTransform(current, action);
            }
        }

        public void Message(object text, string name = null, Color color = default, int size = -1, DateTime time = default)
        {
            ChatMessage message = new ChatMessage(text, name, color, size, time);
            previousTexts.Add(message);
            GameObject newText = Instantiate(textPrefab);
            newText.GetComponent<TMP_Text>().text = $"//// {message.time:dd/MM/yyyy hh:mm:ss tt} [{message.name}]:" +
                $" {ChatUtility.Color(message.color)}{message.text}";
            newText.GetComponent<TMP_Text>().fontSize = message.size;
            newText.transform.SetParent(content);
            ScrollToBottom();
        }
    }
}

[BepInPlugin(modGUID, modName, modVer)]
public class ChatCommandsPlugin : BaseUnityPlugin
{
    internal const string modGUID = "BULLETBOT.ChatCommands";
    internal const string modName = "Chat Commands";
    private const string modVer = "3.1.2";

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
            manager.name = "Chat Commands";
            ChatCommandsManager chat = manager.AddComponent<ChatCommandsManager>();
        });
    }
}