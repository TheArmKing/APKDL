using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using PuppeteerExtraSharp;
using PuppeteerExtraSharp.Plugins.BlockResources;
using PuppeteerExtraSharp.Plugins.ExtraStealth;
using PuppeteerSharp;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace APKDL {
    // config.json structure
    public class Config {
        public string DownloadPath { get; set; }
        public string ExecutablePath { get; set; }
        public bool ShowOBB { get; set; }
        public bool Headless { get; set; }
        public int TimeoutMillisec { get; set; }
    }

    class Program {
        static string finalStr = "";

        // Updates download prohress
        static void webClient_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e) {
            Console.Write("\rDownload progress: {0}%   ", e.ProgressPercentage);
        }

        static void WriteLineExit(string line) {
            Console.WriteLine(line);
            Environment.Exit(0);
        }

        static async Task Main(string[] args) {
            // If config is not there, generate & quit
            if (!File.Exists("config.json")) {
                Config defaultConfig = new Config {
                    DownloadPath = "",
                    ExecutablePath = "",
                    ShowOBB = false,
                    Headless = true,
                    TimeoutMillisec = 20000
                };

                File.WriteAllText("config.json", JsonConvert.SerializeObject(defaultConfig, Formatting.Indented));
                WriteLineExit("Generated config.json, you can edit it to your liking and relaunch!");
            }

            Config config = JsonConvert.DeserializeObject<Config>(File.ReadAllText("config.json"));

            // Check if DownloadPath is valid
            if (!string.IsNullOrEmpty(config.DownloadPath) && !Directory.Exists(config.DownloadPath)) {
                WriteLineExit("Error - Custom Download Path does not exist!");
            }

            // Check if valid ExecutablePath is provided, if not then check & install default Chromium accordingly
            if (string.IsNullOrEmpty(config.ExecutablePath)) {
                BrowserFetcher browserFetcher = new BrowserFetcher();
                if (browserFetcher.LocalRevisions() == null || !browserFetcher.LocalRevisions().GetEnumerator().MoveNext()) {
                    browserFetcher.DownloadProgressChanged += new DownloadProgressChangedEventHandler(webClient_DownloadProgressChanged);
                    Console.WriteLine("Downloading Chromium...");
                    await browserFetcher.DownloadAsync(BrowserFetcher.DefaultChromiumRevision);
                    Console.Clear();
                }
                config.ExecutablePath = browserFetcher.GetExecutablePath(BrowserFetcher.DefaultChromiumRevision);
            } else if (!File.Exists(config.ExecutablePath)) {
                WriteLineExit("Error - Custom Executable Path does not exist!");
            }

            string[] archs = { "default", "armeabi-v7a", "arm64-v8a" };

            // Read package id from input
            string packageID;
            while (true) {
                Console.Write("Package ID: ");
                packageID = Console.ReadLine();
                if (!string.IsNullOrEmpty(packageID)) {
                    break;
                }

            }

            // Read valid arch-type selection from input
            Console.WriteLine($"1. {archs[0]}\n2. {archs[1]}\n3. {archs[2]}");
            int num;
            while (true) {
                Console.Write("Choose an arch type: ");
                var ipt = Console.ReadLine();

                if (Int32.TryParse(ipt, out num)) {
                    if (num > 0 && num < 4) {
                        num--;
                        break;
                    }
                }
            }

            Console.Clear();
            Console.WriteLine("Searching...");

            // Launch Puppeteer & Check if executable is valid (Chrome/Chromium) only
            var stealth = new PuppeteerExtra().Use(new StealthPlugin()).Use(new BlockResourcesPlugin());
            Browser browser = null;
            try {
                browser = await stealth.LaunchAsync(new LaunchOptions { Headless = config.Headless, ExecutablePath = config.ExecutablePath });
            } catch (PuppeteerSharp.ProcessException e) {
                WriteLineExit($"Error when opening browser - {e.Message}");
            }

            // Make new navigation page and go to APKCombo
            Page page = page = await browser.NewPageAsync();
            try {
                await page.GoToAsync($"https://apkcombo.com/apk-downloader/?package={packageID}&arches={archs[num]}");
            } catch (PuppeteerSharp.NavigationException e) {
                WriteLineExit($"Navigation Exception: {e.Message}");
            }

            // If package id is bad then APKCombo redirects it to a search
            if (page.Url.Contains("search?")) {
                WriteLineExit($"Malformed Package ID '{packageID}'");
            }

            // Launch stopwatch to check for timout
            Stopwatch aTimer = new();
            aTimer.Start();

            ElementHandle result = null;
            while (true) {
                // If timed out then exit
                if (aTimer.ElapsedMilliseconds > 20000) {
                    WriteLineExit("Operation Timed Out");
                }

                // Check if download-tab exists
                try {
                    result = await page.WaitForSelectorAsync("div#download-tab", new WaitForSelectorOptions { Timeout = (int)TimeSpan.FromSeconds(1).TotalMilliseconds });
                    var inner = await result.GetPropertyAsync("innerHTML");
                    finalStr = inner.ToString();
                    break;
                } catch (Exception e) {
                    try {
                        // if it doesn't then check if download-result exists
                        result = await page.WaitForSelectorAsync("div#download-result", new WaitForSelectorOptions { Timeout = (int)TimeSpan.FromSeconds(1).TotalMilliseconds });
                        var inner = await result.GetPropertyAsync("innerHTML");
                        finalStr = inner.ToString();
                        // if finalStr is 'JSHandle:' then the page has not loaded yet
                        if (!finalStr.Equals("JSHandle:")) {
                            // download-result contains an error div if apk not found with provided settings
                            if (finalStr.Contains("<div class=\"error\"")) {
                                WriteLineExit($"Couldn't find APK");
                            } else {
                                break;
                            }
                        }
                    } catch (Exception f) { }
                }
            }

            // Close browser and parse the download content
            await browser.CloseAsync();
            var lines = finalStr.Split("\n");
            string arch = "", link = "", name = "", apkType = "";
            bool inLink = false;
            int numLinks = 0;
            List<string> links = new();

            for (int x = 0; x < lines.Length; x++) {
                var line = lines[x];
                if (line.Contains("<span class=\"blur\">")) {
                    arch = line.Split("</code></span>")[0].Split(">")[^1];
                } else if (line.Contains("<figure>")) {
                    link = System.Web.HttpUtility.HtmlDecode(lines[x - 1].Split("\"")[1]); // replaces stuff like &amp; with &
                    inLink = true;
                } else if (inLink && line.Contains("<span class=\"vername\">")) {
                    name = line.Split("</span>")[0].Split(">")[^1];
                } else if (inLink && line.Contains("<span class=\"vtype\">")) {
                    apkType = line.Split("</span></span>")[0].Split(">")[^1];
                } else if (inLink && line.Contains("<span class=\"spec ltr\">")) {
                    inLink = false;
                    if (apkType.Equals("OBB") && !config.ShowOBB) {
                        continue;
                    }
                    var apkSize = line.Split("</span>")[0].Split(">")[^1];
                    links.Add(link);
                    numLinks++;
                    if (numLinks == 1) {
                        Console.Clear();
                        Console.WriteLine("Found the following:\n");
                    }
                    Console.WriteLine(string.Format($"{numLinks}. {arch}\n   Name: {name} ({apkType})\n   Size: {apkSize.Trim()}\n   Link: {link}\n"));
                }
            }

            // Never experienced this but just in case
            if (numLinks == 0) {
                Console.WriteLine("Never expected this, maybe a parsing error?");
            } else {
                // If more than 1 link is found then get which ones to download with comma separate numbers
                List<int> nums = new();
                if (numLinks > 1) {
                    while (true) {
                        Console.Write("Which one do you want to download? : ");
                        var ipt = Regex.Replace(Console.ReadLine(), "[^0-9,]", "").Split(",");
                        foreach (string str in ipt) {
                            if (Int32.TryParse(str, out num)) {
                                if (num > 0 && num <= numLinks) {
                                    num--;
                                    nums.Add(num);
                                }
                            }
                        }

                        if (nums.Count > 0) {
                            break;
                        }
                    }
                } else {
                    nums.Add(0);
                    Console.Write("Enter to download or ^C to quit: ");
                    Console.ReadLine();
                }

                // Download every chosen option
                foreach (int i in nums) {
                    var fileName = Uri.UnescapeDataString(links[i].Split("?")[0].Split("/")[^1]); // replaces stuff like %20 with a whitespace
                    var downloadPath = Path.Combine((string.IsNullOrEmpty(config.DownloadPath) ? AppDomain.CurrentDomain.BaseDirectory : config.DownloadPath), fileName);

                    Console.WriteLine($"\nDownloading to {downloadPath}");

                    using (WebClient wc = new WebClient()) {
                        wc.DownloadProgressChanged += webClient_DownloadProgressChanged;
                        try {
                            await wc.DownloadFileTaskAsync(new Uri(links[i]), downloadPath);
                        } catch (Exception e) {
                            Console.WriteLine($"Errored out while downloading - {e.Message}");
                        }

                    }
                }
                Console.WriteLine("\nAll Done");

            }
        }
    }
}
