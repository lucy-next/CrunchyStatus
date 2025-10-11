using DiscordRPC;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrunchyTray
{
    static class Program
    {
        const string WS_PREFIX = "http://127.0.0.1:54231/";
        const string DISCORD_CLIENT_ID = "1426343468426596535";

        static DiscordRpcClient? discordClient;

        static NotifyIcon trayIcon;
        static ContextMenuStrip trayMenu;
        static ToolStripMenuItem statusTitleItem;
        static ToolStripMenuItem statusItem;
        static ToolStripSeparator sepItem;
        static ToolStripMenuItem exitItem;

        static HttpListener? httpListener;
        static bool isExtensionConnected = false;
        static bool isDiscordConnected = false;
        static string extensionErrorMessage = "";

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            InitTray();
            InitDiscord();
            Task.Run(() => RunWebSocketServer());

            Application.Run();
            ExitClean();
        }

        static void InitTray()
        {
            trayMenu = new ContextMenuStrip();
            statusTitleItem = new ToolStripMenuItem("CrunchyStatus") { Enabled = false };
            trayMenu.Items.Add(statusTitleItem);

            statusItem = new ToolStripMenuItem("Waiting for Extension") { Enabled = false, ForeColor = Color.Orange };
            trayMenu.Items.Add(statusItem);

            sepItem = new ToolStripSeparator();
            trayMenu.Items.Add(sepItem);

            exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => ExitClean();
            trayMenu.Items.Add(exitItem);

            trayIcon = new NotifyIcon
            {
                Icon = LoadIcon("app_icon.ico"),
                ContextMenuStrip = trayMenu,
                Text = "CrunchyStatus",
                Visible = true
            };
        }

        static void UpdateTrayStatus()
        {
            if (!isDiscordConnected)
            {
                statusItem.Text = "Discord not detected";
                statusItem.ForeColor = Color.Red;
            }
            else if (!isExtensionConnected)
            {
                statusItem.Text = "Waiting for Extension";
                statusItem.ForeColor = Color.Orange;
            }
            else if (!string.IsNullOrEmpty(extensionErrorMessage))
            {
                statusItem.Text = "Extension Error";
                statusItem.ForeColor = Color.Red;
            }
            else
            {
                statusItem.Text = "Connected";
                statusItem.ForeColor = Color.Green;
            }

            try { trayIcon.Text = "CrunchyStatus"; }
            catch { }
        }

        static Icon LoadIcon(string filename)
        {
            string path = Path.Combine(Application.StartupPath, filename);
            if (File.Exists(path))
            {
                try { return new Icon(path); }
                catch { MessageBox.Show($"Failed to load icon: {path}"); }
            }
            else MessageBox.Show($"Icon file not found: {path}");
            return SystemIcons.Application;
        }

        static void InitDiscord()
        {
            try
            {
                discordClient = new DiscordRpcClient(DISCORD_CLIENT_ID);
                discordClient.Initialize();
                isDiscordConnected = true;
            }
            catch { isDiscordConnected = false; }
            UpdateTrayStatus();
        }

        // ========== Discord Presence ==========
        static void UpdateDiscordPresence(string details, string state, bool isWatching, long? startTimestamp = null)
        {
            if (discordClient == null) return;

            var assets = new Assets
            {
                LargeImageKey = "icon_app",
                LargeImageText = "Crunchyroll"
            };

            if (isWatching)
            {
                assets.SmallImageKey = "icon_play";
                assets.SmallImageText = "Watching";
            }
            else
            {
                assets.SmallImageKey = null;
                assets.SmallImageText = null;
            }

            var presence = new RichPresence
            {
                Details = details,
                State = state,
                Assets = assets,
                Type = ActivityType.Watching
            };

            // Timestamps only when actually watching
            if (isWatching && startTimestamp.HasValue)
            {
                try
                {
                    DateTime startUtc = DateTimeOffset.FromUnixTimeMilliseconds(startTimestamp.Value).UtcDateTime;
                    presence.Timestamps = new Timestamps { Start = startUtc };
                }
                catch
                {
                    presence.Timestamps = null;
                }
            }
            else
            {
                presence.Timestamps = null;
            }

            try
            {
                discordClient.SetPresence(presence);
            }
            catch
            {
                extensionErrorMessage = "Discord not detected";
                UpdateTrayStatus();
            }
        }

        // ========== WebSocket server ==========
        static async Task RunWebSocketServer()
        {
            httpListener = new HttpListener();
            httpListener.Prefixes.Add(WS_PREFIX);
            try { httpListener.Start(); } catch { extensionErrorMessage = "Cannot start WebSocket"; UpdateTrayStatus(); return; }

            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await httpListener.GetContextAsync(); } catch { break; }

                _ = Task.Run(async () =>
                {
                    if (!ctx.Request.IsWebSocketRequest)
                    {
                        ctx.Response.StatusCode = 400;
                        using var sw = new StreamWriter(ctx.Response.OutputStream);
                        sw.Write("WebSocket only");
                        ctx.Response.Close();
                        return;
                    }

                    HttpListenerWebSocketContext wsCtx;
                    try
                    {
                        wsCtx = await ctx.AcceptWebSocketAsync(null);
                        isExtensionConnected = true;
                        extensionErrorMessage = "";
                        UpdateTrayStatus();
                    }
                    catch
                    {
                        extensionErrorMessage = "Extension Error";
                        UpdateTrayStatus();
                        try { ctx.Response.Close(); } catch { }
                        return;
                    }

                    var ws = wsCtx.WebSocket;
                    var buffer = new byte[8192];

                    while (ws.State == WebSocketState.Open)
                    {
                        try
                        {
                            var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                            if (res.MessageType == WebSocketMessageType.Close)
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                                isExtensionConnected = false;
                                UpdateTrayStatus();
                                break;
                            }

                            var msg = Encoding.UTF8.GetString(buffer, 0, res.Count);
                            HandleMessage(msg);
                        }
                        catch
                        {
                            extensionErrorMessage = "Extension Error";
                            UpdateTrayStatus();
                            break;
                        }
                    }

                    try { ws.Dispose(); } catch { }
                });
            }
        }

        // ===== JSON helpers =====
        static string SafeGetString(JsonElement parent, string propName)
        {
            if (parent.ValueKind != JsonValueKind.Object) return "";
            if (!parent.TryGetProperty(propName, out var el)) return "";
            if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? "";
            if (el.ValueKind == JsonValueKind.Number) return el.GetRawText();
            return "";
        }

        static long? SafeGetLongNullable(JsonElement parent, string propName)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(propName, out var el)) return null;
            if (el.ValueKind == JsonValueKind.Number)
            {
                if (el.TryGetInt64(out long v)) return v;
                if (long.TryParse(el.GetRawText(), out long pv)) return pv;
            }
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (long.TryParse(s, out long pv)) return pv;
                if (double.TryParse(s, out double pd)) return (long)Math.Floor(pd);
            }
            return null;
        }

        // ===== Main JSON handler =====
        static void HandleMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string displayState = "Browsing";
                if (root.TryGetProperty("displayState", out var ds) && ds.ValueKind == JsonValueKind.String)
                    displayState = ds.GetString() ?? "Browsing";

                string displayTitle = "";
                if (root.TryGetProperty("displayTitle", out var dt) && dt.ValueKind == JsonValueKind.String)
                    displayTitle = dt.GetString() ?? "";

                bool isWatching = string.Equals(displayState, "Watching", StringComparison.OrdinalIgnoreCase);

                string animeTitle = "";
                string episodeTitle = "";
                if (root.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    animeTitle = SafeGetString(meta, "title");
                    episodeTitle = SafeGetString(meta, "episode");
                }

                long? startTimestamp = SafeGetLongNullable(root, "timestamp");

                string detailsText;
                string stateText;

                switch (displayState)
                {
                    case "Watching":
                        detailsText = !string.IsNullOrEmpty(animeTitle) ? $"Watching {animeTitle}" : "Watching Anime";
                        stateText = !string.IsNullOrEmpty(episodeTitle) ? episodeTitle : "";
                        break;

                    case "Looking":
                        {
                            var cleanTitle = displayTitle ?? "";
                            cleanTitle = Regex.Replace(cleanTitle, @"^Watch\s+", "", RegexOptions.IgnoreCase).Trim();
                            cleanTitle = Regex.Replace(cleanTitle, @"\s*-\s*Crunchyroll\s*$", "", RegexOptions.IgnoreCase).Trim();
                            detailsText = "Looking at";
                            stateText = cleanTitle;
                        }
                        break;

                    default:
                        detailsText = "Browsing Crunchyroll";
                        stateText = "";
                        break;
                }

                UpdateDiscordPresence(detailsText, stateText, isWatching, startTimestamp);
                extensionErrorMessage = "";
                UpdateTrayStatus();
            }
            catch (Exception ex)
            {
                extensionErrorMessage = "Extension Error";
                Console.WriteLine("HandleMessage error: " + ex);
                UpdateTrayStatus();
            }
        }

        static void ExitClean()
        {
            try { discordClient?.Dispose(); } catch { }
            try { httpListener?.Stop(); } catch { }
            try { trayIcon.Visible = false; trayIcon.Dispose(); } catch { }
            Environment.Exit(0);
        }
    }
}
