using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using SteamKit2;
using Newtonsoft.Json;
using Spectre.Console;
using System.Net.Http;
using System.Collections.Specialized;
using static SteamKit2.Internal.CMsgRemoteClientBroadcastStatus;
using SteamKit2.Internal;

namespace HelpBot
{
    class Program
    {
        static bool debugMode = true;
        static int messageScope = 5;

        public static SteamClient steamClient;
        static CallbackManager manager;
        static SteamUser steamUser;
        static SteamFriends steamFriends;
        static SteamTrading steamTrading;
        public static List<dynamic> chatHistory = new List<dynamic>();
        public static List<INFO> users = new List<INFO>();
        static string iniFilePath = Path.Combine(Directory.GetCurrentDirectory(), "config.ini");
        static NameValueCollection appSettings;
        static bool isRunning;
        static string authCode, twoFactorAuth;

        // User defined
        static string url, key, user, pass;
        static SteamID groupNumber;
        static string consoleTitle;
        static string steamPersona;
        static string gptModel;
        static string greetingMessage;
        static string customPlayingGameName;
        static bool showPlayingCustomGame;
        static bool setPersona;
        static bool inviteToGroupWhenAdded;
        static bool sendGreetingMessageWhenAdded;


        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            if (consoleTitle is null)
            {
                Console.Title = "ChatGPT Steam Bot";
            } else
            {
                Console.Title = consoleTitle;
            }

            steamClient = new SteamClient();

            manager = new CallbackManager(steamClient);

            steamUser = steamClient.GetHandler<SteamUser>();

            steamFriends = steamClient.GetHandler<SteamFriends>();

            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

            manager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
            manager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);
            manager.Subscribe<SteamFriends.FriendMsgCallback>(ChatRespond);

            manager.Subscribe<SteamTrading.TradeProposedCallback>(tradeProposed);

            isRunning = true;

            EnsureChatLogsDirectoryExists();
            InitializeSettings();
            LogEvent("Connecting to Steam...");

            steamClient.Connect();

            while (isRunning)
            {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        }

        static void InitializeSettings()
        {
            if (!File.Exists(iniFilePath))
            {
                using (StreamWriter writer = new StreamWriter(iniFilePath))
                {
                    writer.WriteLine("[Settings]");
                    writer.WriteLine("URL=https://api.openai.com/v1/chat/completions");
                    writer.WriteLine("APIKey=PLACEHOLDER");
                    writer.WriteLine("SteamUsername=PLACEHOLDER");
                    writer.WriteLine("SteamPassword=PLACEHOLDER");
                    writer.WriteLine("GroupNumber=PLACEHOLDER");
                    writer.WriteLine("ConsoleTitle=ChatGPT SteamBot");
                    writer.WriteLine("SteamPersona=ChatBot");
                    writer.WriteLine("GptModel=gpt-3.5-turbo");
                    writer.WriteLine("GreetingMessage=Hello! I'm a GPT3.5-Powered chat bot. Ask me anything!");
                    writer.WriteLine("CustomPlayingGameName=I'm online, you can message me!");
                    writer.WriteLine("ShowPlayingCustomGame=false");
                    writer.WriteLine("SetPersona=true");
                    writer.WriteLine("InviteToGroupWhenAdded=false");
                    writer.WriteLine("SendGreetingMessageWhenAdded=true");
                }
            }

            appSettings = new NameValueCollection();
            var lines = File.ReadAllLines(iniFilePath);

            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith(";") && line.Contains('='))
                {
                    var splitLine = line.Split('=');
                    appSettings.Add(splitLine[0].Trim(), splitLine[1].Trim());
                }
            }

            // Load settings
            key = appSettings["APIKey"] ?? "PLACEHOLDER";
            user = appSettings["SteamUsername"] ?? "PLACEHOLDER";
            pass = appSettings["SteamPassword"] ?? "PLACEHOLDER";
            url = appSettings["URL"] ?? "https://api.openai.com/v1/chat/completions";

            inviteToGroupWhenAdded = Convert.ToBoolean(appSettings["InviteToGroupWhenAdded"] ?? "false");

            string groupNumberStr = appSettings["GroupNumber"] ?? "PLACEHOLDER"; // Default group number
            if (UInt64.TryParse(groupNumberStr, out ulong groupNumberVal))
            {
                groupNumber = new SteamID(groupNumberVal);
            }
            else if (inviteToGroupWhenAdded)
            {
                inviteToGroupWhenAdded = false;
                LogError($"Invalid format for GroupNumber in INI file: {groupNumberStr}");
            }

            consoleTitle = appSettings["ConsoleTitle"] ?? "PLACEHOLDER";
            steamPersona = appSettings["SteamPersona"] ?? "ChatBot";
            gptModel = appSettings["GptModel"] ?? "gpt-3.5-turbo";
            greetingMessage = appSettings["GreetingMessage"] ?? "Hello! I'm a GPT3.5-Powered chat bot. Ask me anything!";
            customPlayingGameName = appSettings["CustomPlayingGameName"] ?? "Hello! I'm a GPT3.5-Powered chat bot. Ask me anything!";
            showPlayingCustomGame = Convert.ToBoolean(appSettings["ShowPlayingCustomGame"] ?? "false");
            setPersona = Convert.ToBoolean(appSettings["SetPersona"] ?? "true");
            sendGreetingMessageWhenAdded = Convert.ToBoolean(appSettings["SendGreetingMessageWhenAdded"] ?? "true");
        }

        static void EnsureChatLogsDirectoryExists()
        {
            string chatLogsPath = Path.Combine(Directory.GetCurrentDirectory(), "ChatLogs");
            if (!Directory.Exists(chatLogsPath))
            {
                Directory.CreateDirectory(chatLogsPath);
            }
        }

        static void tradeProposed(SteamTrading.TradeProposedCallback callback)
        {
            steamTrading.RespondToTrade(callback.TradeID, true);
            LogEvent("Accepted Trade!");
        }

        static void OnConnected(SteamClient.ConnectedCallback callback)
        {
            // Check if the OpenAPI key is missing
            if (string.IsNullOrEmpty(key) || key == "PLACEHOLDER")
            {
                LogError("An OpenAI API key was not set in the config.ini file!");
            }

            // Check if username or password is missing
            if (string.IsNullOrEmpty(user) || user == "PLACEHOLDER" ||
                string.IsNullOrEmpty(pass) || pass == "PLACEHOLDER")
            {
                LogError("Steam username or password is not set in the config.ini file. Cannot log into Steam.");
                return;
            }

            LogEvent("Connected to Steam! Logging in as " + user);

            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = user,
                Password = pass,
                AuthCode = authCode,          // For SteamGuard
                TwoFactorCode = twoFactorAuth // For 2FA
            });
        }

        static void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Console.WriteLine("Disconnected from Steam, reconnecting in 5...");

            Thread.Sleep(TimeSpan.FromSeconds(5));

            steamClient.Connect();
        }

        static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            bool isSteamGuard = callback.Result == EResult.AccountLogonDenied;
            bool is2FA = callback.Result == EResult.AccountLoginDeniedNeedTwoFactor;

            if (isSteamGuard || is2FA)
            {
                LogError("This account is SteamGuard protected!");

                if (is2FA)
                {
                    Console.Write("Please enter your 2 factor auth code from your authenticator app: ");
                    twoFactorAuth = Console.ReadLine();
                }
                else
                {
                    Console.Write("Please enter the auth code sent to the email at {0}: ", callback.EmailDomain);
                    authCode = Console.ReadLine();
                }

                return;
            }

            if (callback.Result != EResult.OK)
            {
                Console.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);
                Console.ReadKey();

                isRunning = false;
                return;
            }
            Console.Clear();

            if (setPersona)
            {
                steamFriends.SetPersonaName(steamPersona);
            }
            steamFriends.SetPersonaState(EPersonaState.Online);

            string botPersona = steamFriends.GetPersonaName();
            string botSteamID = steamUser.SteamID.ToString();

            LogEvent($"Successfully logged into Steam!");
            LogEvent($"Your public name is: [{botPersona}]");
            LogEvent($"With SteamID: [{botSteamID}]");

            if (showPlayingCustomGame)
            {
                PlayGame(customPlayingGameName);
            }
        }

        static void PlayGame(string gameName)
        {
            var request = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

            var gamePlayed = new CMsgClientGamesPlayed.GamePlayed();
            if (!string.IsNullOrEmpty(gameName))
            {
                gamePlayed.game_id = new GameID()
                {
                    AppType = GameID.GameType.Shortcut,
                    ModID = uint.MaxValue

                };
                gamePlayed.game_extra_info = gameName;
            }

            request.Body.games_played.Add(gamePlayed);

            Program.steamClient.Send(request);

        }

        static void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            steamFriends.SetPersonaState(EPersonaState.Online);
        }

        static void OnFriendsList(SteamFriends.FriendsListCallback callback)
        {
            foreach (var friend in callback.FriendList)
            {
                if (friend.Relationship == EFriendRelationship.RequestRecipient)
                {
                    steamFriends.AddFriend(friend.SteamID);

                    // Wait for a moment to let Steam API update the friend's information
                    Thread.Sleep(500);

                    string friendName = steamFriends.GetFriendPersonaName(friend.SteamID);

                    if (string.IsNullOrEmpty(friendName) || friendName == "[unknown]")
                    {
                        // LogEvent($"New friend (ID: {friend.SteamID}) has been added, name pending update.");
                    }
                    else
                    {
                        LogEvent($"{friendName} has been added!");

                        // Send a greeting message when added
                        if (sendGreetingMessageWhenAdded)
                        {
                            steamFriends.SendChatMessage(friend.SteamID, EChatEntryType.ChatMsg, greetingMessage);
                            LogMessage($"Sent Message to [{friendName}]: {greetingMessage}", true);
                        }

                        // Invite to group if enabled and groupNumber is valid
                        if (inviteToGroupWhenAdded && groupNumber.IsValid)
                        {
                            try
                            {
                                InviteUserToGroup(friend.SteamID, groupNumber);
                            }
                            catch (Exception ex)
                            {
                                LogDebug($"Error inviting {friendName} to group: {ex.Message}");
                            }
                        }
                    }
                }
            }
        }

        public static void InviteUserToGroup(SteamID userID, SteamID groupID)
        {
            var InviteUser = new ClientMsg<CMsgInviteUserToGroup>((int)EMsg.ClientInviteUserToClan);

            InviteUser.Body.GroupID = groupID.ConvertToUInt64();
            InviteUser.Body.Invitee = userID.ConvertToUInt64();
            InviteUser.Body.UnknownInfo = true;

            Program.steamClient.Send(InviteUser);
            LogEvent($"Inviting {steamFriends.GetFriendPersonaName(userID)} to group {groupID}.");
        }

        static IEnumerable<string> ReadLinesFromFileEnd(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Position = fs.Length;
                StringBuilder line = new StringBuilder();
                for (long i = fs.Length - 1; i >= 0; i--)
                {
                    fs.Position = i;
                    int ch = fs.ReadByte();
                    if (ch == '\n' && line.Length > 0)
                    {
                        yield return ReverseString(line.ToString());
                        line.Clear();
                        continue;
                    }
                    line.Append((char)ch);
                }
                if (line.Length > 0)
                    yield return ReverseString(line.ToString());
            }
        }

        static string ReverseString(string s)
        {
            char[] charArray = s.ToCharArray();
            Array.Reverse(charArray);
            return new string(charArray);
        }

        static async Task<string> GetGptResponse(SteamID steamID, string message)
        {
            string fileName = steamID.AccountID.ToString();
            string chatLogPath = Path.Combine(Directory.GetCurrentDirectory(), "ChatLogs", $"{fileName}.txt");
            List<string> recentMessages = new List<string>();

            if (File.Exists(chatLogPath))
            {
                string[] allLines = File.ReadAllLines(chatLogPath);
                // Start from the end of the file and go backwards, but skip the first line (user info)
                for (int i = allLines.Length - 1; i > 0 && recentMessages.Count < messageScope; i--)
                {
                    var line = allLines[i];
                    var parts = line.Split('|');
                    if (parts.Length == 3)
                    {
                        recentMessages.Insert(0, parts[2]); // Insert at beginning to maintain order
                    }
                }
            }

            // Debug: Log the collected recent messages
            // LogDebug("Recent Messages: " + string.Join(" | ", recentMessages));

            // Combine recent messages with the current message
            var messages = recentMessages.Select(msg => new { role = "user", content = msg }).ToList();
            messages.Add(new { role = "user", content = message });

            var request = new
            {
                messages,
                model = gptModel,
                max_tokens = 300,
            };

            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");
            var requestJson = JsonConvert.SerializeObject(request);

            // Debug: Log the full API request JSON
            LogDebug("API Request JSON: " + requestJson);

            var requestContent = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

            try
            {
                var httpResponseMessage = await httpClient.PostAsync(url, requestContent);
                var jsonString = await httpResponseMessage.Content.ReadAsStringAsync();
                var responseObject = JsonConvert.DeserializeAnonymousType(jsonString, new
                {
                    choices = new[] { new { message = new { role = string.Empty, content = string.Empty } } },
                    error = new { message = string.Empty }
                });

                if (!string.IsNullOrEmpty(responseObject?.error?.message))
                {
                    return "Error: " + responseObject.error.message;
                }
                else
                {
                    return responseObject.choices[0].message.content;
                }
            }
            catch (Exception ex)
            {
                return "Exception occurred: " + ex.Message;
            }
        }

        static async void ChatRespond(SteamFriends.FriendMsgCallback callback)
        {
            if (callback.EntryType == EChatEntryType.ChatMsg)
            {
                string displayName = steamFriends.GetFriendPersonaName(callback.Sender);
                string receivedMessageLog = $"Received message from [{displayName}] [{callback.Sender}]: {callback.Message}";
                LogMessage(receivedMessageLog, isSent: false);

                // Write received message to user-specific chat log
                WriteToUserChatLog(callback.Sender, callback.Message, false);

                try
                {
                    var response = await GetGptResponse(callback.Sender, callback.Message);
                    steamFriends.SendChatMessage(callback.Sender, EChatEntryType.ChatMsg, response);

                    string sentMessageLog = $"Sent response to [{displayName}] [{callback.Sender}]: {response}";
                    LogMessage(sentMessageLog, isSent: true);

                    // Write sent message to user-specific chat log
                    WriteToUserChatLog(callback.Sender, response, true);
                }
                catch (Exception ex)
                {
                    LogError($"Error responding to [{displayName}] [{callback.Sender}]: {ex.Message}");
                }
            }
        }

        static void WriteToUserChatLog(SteamID steamID, string message, bool isSent)
        {
            string fileName = steamID.AccountID.ToString(); // Using AccountID for simplicity
            string chatLogPath = Path.Combine(Directory.GetCurrentDirectory(), "ChatLogs", $"{fileName}.txt");

            // Check if the file exists. If not, write the initial user information.
            if (!File.Exists(chatLogPath))
            {
                string userInfo = $"User Information: Alias Name = '{steamFriends.GetFriendPersonaName(steamID)}', SteamID = '{steamID}'";
                File.WriteAllText(chatLogPath, userInfo + Environment.NewLine);
            }

            string sender = isSent ? "Bot" : "User";
            string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}|{sender}|{message}";

            try
            {
                File.AppendAllText(chatLogPath, logEntry + Environment.NewLine);
            }
            catch (Exception ex)
            {
                LogError($"Error writing to chat log: {ex.Message}");
            }
        }

        static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Console.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        static void LogMessage(string message, bool isSent)
        {
            var color = isSent ? "blue" : "green";
            AnsiConsole.MarkupLine($"[grey]{DateTime.Now}[/] - [{color}]{message.EscapeMarkup()}[/]");
            chatHistory.Add(new { Timestamp = DateTime.Now, Message = message, IsSent = isSent });
        }

        static void LogError(string message)
        {
            AnsiConsole.MarkupLine($"[grey]{DateTime.Now}[/] - [red]{message.EscapeMarkup()}[/]");
            chatHistory.Add(new { Timestamp = DateTime.Now, Error = message });
        }

        static void LogEvent(string message)
        {
            AnsiConsole.MarkupLine($"[grey]{DateTime.Now}[/] - [Purple]{message.EscapeMarkup()}[/]");
            chatHistory.Add(new { Timestamp = DateTime.Now, Error = message });
        }

        static void LogDebug(string message)
        {
            if (debugMode)
            {
                string debugLogPath = Path.Combine(Directory.GetCurrentDirectory(), "ChatLogs", "DebugLog.txt");

                try
                {
                    using (StreamWriter writer = new StreamWriter(debugLogPath, true))
                    {
                        writer.WriteLine($"[{DateTime.Now}] DEBUG: {message}");
                    }

                    // Optionally, display the debug message in the console as well
                    AnsiConsole.MarkupLine($"[grey][[{DateTime.Now}]] [yellow]DEBUG: {message.EscapeMarkup()}[/][/]");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error writing to debug log: {ex.Message}");
                }
            }
        }

        public class INFO
        {
            public SteamID sid { get; set; }
            public bool canRequest { get; set; }
        }
    }
}
