using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using Microsoft.Extensions.Logging;
using System.IO;
using OpenQA.Selenium.Support.UI;
using OpenQA.Selenium.Interactions;

namespace Recast.WindowsRecorder.Services
{
    public class SessionManager
    {
        public class Session
        {
            public string Controller { get; init; } = string.Empty;
            public string Mode { get; init; } = "manual";
            public IWebDriver Driver { get; init; } = default!;
            public CancellationTokenSource? Cts { get; init; }
            public Task? WatchdogTask { get; init; }
        }

        private readonly ConcurrentDictionary<string, Session> _sessions = new();
        private static readonly string[] _controllers = new[] { "youtube", "videojs", "generic" };

        public IEnumerable<string> ControllerNames => _controllers;
        public bool HasAny => !_sessions.IsEmpty;

        public Session? Get(string name) => _sessions.TryGetValue(name, out var s) ? s : null;

        private readonly ILogger<SessionManager> _log;

        public SessionManager(ILogger<SessionManager> log)
        {
            _log = log;
        }

        public async Task<bool> LaunchAsync(string controller, string url, string mode, bool keepOpen)
        {
            try
            {
                await StopAsync(controller);

                var options = new ChromeOptions();
                options.AddArgument("--window-size=1920,1080");
                options.AddArgument("--window-position=0,0");
                options.AddArgument("--disable-gpu");
                options.AddArgument("--no-sandbox");
                options.AddArgument("--disable-dev-shm-usage");
                options.AddArgument("--no-default-browser-check");
                options.AddArgument("--no-first-run");
                options.AddArgument("--autoplay-policy=no-user-gesture-required");
                // Hide automation banner and restore prompts
                try { options.AddExcludedArgument("enable-automation"); } catch { }
                try { options.AddAdditionalOption("useAutomationExtension", false); } catch { }
                options.AddArgument("--disable-infobars");
                options.AddArgument("--disable-session-crashed-bubble");
                options.AddArgument("--restore-last-session=false");
                options.AddArgument("--homepage=about:blank");
                // Persistent profile per controller
                var profileRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "recast", ".recast-chrome", controller);
                Directory.CreateDirectory(profileRoot);
                options.AddArgument($"--user-data-dir={profileRoot}");
                options.AddArgument("--profile-directory=Default");

                // Increase logging
                options.AddArgument("--enable-logging=stderr");
                options.AddArgument("--v=1");

                try
                {
                    _log.LogInformation("Launching Chrome: controller={Controller} mode={Mode} url={Url} args={Args}", controller, mode, url, string.Join(' ', options.Arguments));
                    var driver = new ChromeDriver(options);
                    try
                    {
                        driver.Navigate().GoToUrl(url);
                    }
                    catch (Exception navEx)
                    {
                        _log.LogWarning(navEx, "Navigation failed");
                    }

                    if (mode == "automated")
                    {
                        try
                        {
                            if (controller == "videojs")
                            {
                                try { PlayAndFullscreenVideoJs(driver, _log); } catch { }
                            }
                            else
                            {
                                driver.ExecuteScript("(function(){var v=document.querySelector('video'); if(v){v.muted=false; v.volume=1.0; v.play().catch(()=>{}); if(!document.fullscreenElement){try{if(v.requestFullscreen) v.requestFullscreen().catch(()=>{});}catch(e){} var btn=[...document.querySelectorAll('button')].find(b=>/full/i.test((b.textContent||''))||/full/i.test((b.getAttribute('aria-label')||''))); if(btn) try{btn.click();}catch(e){} } } })();");
                                try { var body = driver.FindElement(By.TagName("body")); body?.SendKeys("f"); } catch { }
                            }
                        }
                        catch (Exception autoEx) { _log.LogDebug(autoEx, "automation script failed"); }
                    }

                    CancellationTokenSource? cts = null;
                    Task? watchdog = null;
                    if (mode == "automated" && controller == "videojs")
                    {
                        cts = new CancellationTokenSource();
                        var token = cts.Token;
                        var log = _log; // capture for closure
                        watchdog = Task.Run(async () =>
                        {
                            double lastCt = -1;
                            DateTime lastProgress = DateTime.UtcNow;
                            DateTime lastFullscreenAttempt = DateTime.MinValue;
                            int consecutiveNoVideo = 0;
                            bool inVideoFrame = false; // track if we've successfully switched to video frame
                            const int StallThresholdSeconds = 15;
                            const int NoVideoThreshold = 5; // require 5 consecutive checks (~10s) with no video before considering stall
                            const int FullscreenRetrySeconds = 30; // only retry fullscreen every 30s to avoid disruption
                            
                            log.LogInformation("[Watchdog] Started for controller={Controller}", controller);
                            
                            while (!token.IsCancellationRequested)
                            {
                                try
                                {
                                    await Task.Delay(2000, token);
                                }
                                catch (OperationCanceledException) { break; }
                                catch { }
                                if (token.IsCancellationRequested) break;
                                
                                try
                                {
                                    // Only switch frames if we haven't found the video yet, or if we're getting no video
                                    if (!inVideoFrame || consecutiveNoVideo >= 2)
                                    {
                                        try 
                                        { 
                                            inVideoFrame = SwitchToFrameWithVideo(driver); 
                                        } 
                                        catch { inVideoFrame = false; }
                                    }
                                    
                                    var ctObj = ((IJavaScriptExecutor)driver).ExecuteScript("var v=document.querySelector('video'); return v? v.currentTime: -1;");
                                    double ct = -1;
                                    try { ct = Convert.ToDouble(ctObj, CultureInfo.InvariantCulture); } catch { ct = -1; }
                                    
                                    var now = DateTime.UtcNow;
                                    var stalledDuration = now - lastProgress;
                                    
                                    if (ct < 0)
                                    {
                                        // No video element found
                                        consecutiveNoVideo++;
                                        log.LogDebug("[Watchdog] No video element found (consecutiveNoVideo={Count}, stalledFor={Stalled:F1}s)", 
                                            consecutiveNoVideo, stalledDuration.TotalSeconds);
                                        
                                        // Only consider stall if we've had no video for multiple checks AND exceeded threshold
                                        if (consecutiveNoVideo >= NoVideoThreshold && stalledDuration >= TimeSpan.FromSeconds(StallThresholdSeconds))
                                        {
                                            log.LogWarning("[Watchdog] STALL DETECTED: No video element for {Count} checks, stalledFor={Stalled:F1}s - triggering reload", 
                                                consecutiveNoVideo, stalledDuration.TotalSeconds);
                                            await PerformStallRecovery(driver, log, token);
                                            lastCt = -1; // Reset after reload so any valid time is accepted
                                            lastProgress = DateTime.UtcNow;
                                            consecutiveNoVideo = 0;
                                        }
                                    }
                                    else
                                    {
                                        // Video element found
                                        consecutiveNoVideo = 0;
                                        
                                        // Check if video is progressing
                                        // Accept progress if: first valid reading OR time increased OR time reset (e.g., after reload/seek)
                                        bool isProgressing = lastCt < 0 || ct > lastCt + 0.1 || ct < lastCt - 1.0; // allow for seeks/reloads
                                        
                                        if (isProgressing)
                                        {
                                            if (lastCt >= 0 && Math.Abs(ct - lastCt) > 0.5)
                                            {
                                                log.LogDebug("[Watchdog] Video progressing: currentTime={Ct:F2}s (was {LastCt:F2}s)", ct, lastCt);
                                            }
                                            lastCt = ct;
                                            lastProgress = now;
                                        }
                                        else
                                        {
                                            // Video not progressing
                                            log.LogDebug("[Watchdog] Video NOT progressing: currentTime={Ct:F2}s, lastCt={LastCt:F2}s, stalledFor={Stalled:F1}s", 
                                                ct, lastCt, stalledDuration.TotalSeconds);
                                            
                                            if (stalledDuration >= TimeSpan.FromSeconds(StallThresholdSeconds))
                                            {
                                                log.LogWarning("[Watchdog] STALL DETECTED: Video stuck at {Ct:F2}s for {Stalled:F1}s - triggering reload", 
                                                    ct, stalledDuration.TotalSeconds);
                                                await PerformStallRecovery(driver, log, token);
                                                lastCt = -1; // Reset after reload
                                                lastProgress = DateTime.UtcNow;
                                            }
                                        }
                                    }
                                    
                                    // Check fullscreen status (separate from stall detection)
                                    // Only check/retry fullscreen periodically to avoid disrupting playback
                                    var timeSinceLastFsAttempt = DateTime.UtcNow - lastFullscreenAttempt;
                                    if (timeSinceLastFsAttempt.TotalSeconds >= FullscreenRetrySeconds)
                                    {
                                        try
                                        {
                                            // Switch back to default content to check fullscreen
                                            try { driver.SwitchTo().DefaultContent(); } catch { }
                                            var fs = ((IJavaScriptExecutor)driver).ExecuteScript("return !!document.fullscreenElement;");
                                            bool isFs = false; 
                                            try { isFs = fs is bool b && b; } catch { }
                                            if (!isFs)
                                            {
                                                log.LogInformation("[Watchdog] Not in fullscreen, attempting to restore (last attempt {Ago:F0}s ago)", timeSinceLastFsAttempt.TotalSeconds);
                                                lastFullscreenAttempt = DateTime.UtcNow;
                                                inVideoFrame = false; // will need to re-find frame after this
                                                try { PlayAndFullscreenVideoJs(driver, log); } catch { }
                                            }
                                        }
                                        catch (Exception fsEx) 
                                        { 
                                            log.LogDebug(fsEx, "[Watchdog] Fullscreen check failed"); 
                                        }
                                    }
                                }
                                catch (Exception ex) 
                                { 
                                    log.LogDebug(ex, "[Watchdog] Iteration error"); 
                                }
                            }
                            
                            log.LogInformation("[Watchdog] Stopped for controller={Controller}", controller);
                        }, cts.Token);
                    }

                    var s = new Session { Controller = controller, Mode = mode, Driver = driver, Cts = cts, WatchdogTask = watchdog };
                    _sessions[controller] = s;
                    return true;
                }
                catch (Exception drvEx)
                {
                    _log.LogError(drvEx, "ChromeDriver start failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LaunchAsync failed");
                return false;
            }
        }

        public Task StopAsync(string? controller)
        {
            if (string.IsNullOrWhiteSpace(controller)) return Task.CompletedTask;
            if (_sessions.TryRemove(controller, out var s))
            {
                try { s.Cts?.Cancel(); } catch { }
                try { s.WatchdogTask?.Wait(500); } catch { }
                try { s.Driver.Quit(); } catch { }
                try { s.Driver.Dispose(); } catch { }
            }
            return Task.CompletedTask;
        }

        public Task StopAllAsync()
        {
            foreach (var key in _sessions.Keys.ToArray())
                _ = StopAsync(key);
            return Task.CompletedTask;
        }

        private static async Task PerformStallRecovery(IWebDriver driver, ILogger log, CancellationToken token)
        {
            log.LogInformation("[Watchdog] Performing stall recovery: reloading page");
            try 
            { 
                driver.SwitchTo().DefaultContent();
                ((IJavaScriptExecutor)driver).ExecuteScript("location.reload();"); 
            } 
            catch (Exception ex) 
            { 
                log.LogWarning(ex, "[Watchdog] Reload failed"); 
            }
            
            try { await Task.Delay(3000, token); } 
            catch (OperationCanceledException) { return; }
            
            log.LogInformation("[Watchdog] Attempting to restore playback and fullscreen");
            try 
            { 
                PlayAndFullscreenVideoJs(driver, log); 
                log.LogInformation("[Watchdog] Stall recovery completed");
            } 
            catch (Exception ex) 
            { 
                log.LogWarning(ex, "[Watchdog] PlayAndFullscreen after reload failed"); 
            }
        }

        private static bool SwitchToFrameWithVideo(IWebDriver driver)
        {
            try { driver.SwitchTo().DefaultContent(); } catch { }
            try
            {
                var frames = driver.FindElements(By.TagName("iframe"));
                for (int i = 0; i < frames.Count; i++)
                {
                    try
                    {
                        driver.SwitchTo().Frame(frames[i]);
                        bool hasVideo = false;
                        try { hasVideo = (bool)(((IJavaScriptExecutor)driver).ExecuteScript("return !!document.querySelector('video, .video-js')") ?? false); } catch { hasVideo = false; }
                        if (hasVideo) return true;
                        try { driver.SwitchTo().DefaultContent(); } catch { }
                    }
                    catch { try { driver.SwitchTo().DefaultContent(); } catch { } }
                }
            }
            catch { }
            return false;
        }

        private static bool PlayAndFullscreenVideoJs(IWebDriver driver, ILogger? log = null)
        {
            try
            {
                log?.LogInformation("[VideoJS] Starting PlayAndFullscreenVideoJs");
                
                try { SwitchToFrameWithVideo(driver); } catch { }
                IWebElement player = null;
                try
                {
                    log?.LogDebug("[VideoJS] Waiting for player element (.video-js, .vjs-controls-enabled)");
                    var wait = new WebDriverWait(new SystemClock(), driver, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(200));
                    player = wait.Until(d =>
                    {
                        try { return d.FindElement(By.CssSelector(".video-js, .vjs-controls-enabled")); } catch { return null; }
                    });
                }
                catch (Exception ex) { log?.LogWarning(ex, "[VideoJS] Failed to find player element"); }
                
                if (player == null)
                {
                    log?.LogWarning("[VideoJS] Player element not found, aborting");
                    return false;
                }
                log?.LogInformation("[VideoJS] Player element found");
                
                IWebElement container = player;
                try { container = player.FindElement(By.XPath("./ancestor-or-self::div[contains(@class,'video-js')]")); } catch { }
                
                try
                {
                    log?.LogDebug("[VideoJS] Waiting for loader to disappear");
                    var waitLoader = new WebDriverWait(new SystemClock(), driver, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));
                    waitLoader.Until(d =>
                    {
                        try
                        {
                            var els = d.FindElements(By.CssSelector("img[src*='loader-ftv']"));
                            if (els == null || els.Count == 0) return true;
                            try { return !els[0].Displayed; } catch { return true; }
                        }
                        catch { return true; }
                    });
                }
                catch { }
                
                try
                {
                    log?.LogDebug("[VideoJS] Looking for big play button");
                    var waitBtn = new WebDriverWait(new SystemClock(), driver, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));
                    var playBtn = waitBtn.Until(d =>
                    {
                        try
                        {
                            var el = d.FindElement(By.CssSelector("button.vjs-big-play-button[title='Play Video']"));
                            return el.Enabled ? el : null;
                        }
                        catch { return null; }
                    });
                    if (playBtn != null)
                    {
                        log?.LogInformation("[VideoJS] Clicking big play button");
                        playBtn.Click();
                    }
                    else
                    {
                        log?.LogInformation("[VideoJS] No play button found, clicking container");
                        try { container.Click(); } catch { }
                    }
                }
                catch { try { container.Click(); } catch { } }
                
                try
                {
                    log?.LogDebug("[VideoJS] Waiting for video readyState > 0");
                    var waitReady = new WebDriverWait(new SystemClock(), driver, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
                    waitReady.Until(d =>
                    {
                        try { return (bool)(((IJavaScriptExecutor)d).ExecuteScript("return document.querySelector('video') && document.querySelector('video').readyState > 0") ?? false); } catch { return true; }
                    });
                    log?.LogInformation("[VideoJS] Video is ready (readyState > 0)");
                }
                catch (Exception ex) { log?.LogWarning(ex, "[VideoJS] Timeout waiting for video readyState"); }

                // Enter fullscreen using robust multi-strategy approach (ported from Linux videojs controller)
                // Strategy 1: Try native video fullscreen API first (most reliable)
                log?.LogInformation("[VideoJS] Starting fullscreen sequence");
                bool fsOk = false;
                string fsStrategy = "none";
                try
                {
                    log?.LogInformation("[VideoJS] Strategy 1: Trying native video.requestFullscreen()");
                    var fsResult = ((IJavaScriptExecutor)driver).ExecuteScript(@"
                        (function() {
                            var video = document.querySelector('video');
                            if (!video) return 'no_video';
                            
                            // Try to fullscreen the video element directly
                            if (video.requestFullscreen) {
                                video.requestFullscreen();
                                return 'success_standard';
                            } else if (video.webkitRequestFullscreen) {
                                video.webkitRequestFullscreen();
                                return 'success_webkit';
                            } else if (video.webkitEnterFullscreen) {
                                // iOS Safari
                                video.webkitEnterFullscreen();
                                return 'success_webkit_enter';
                            } else if (video.msRequestFullscreen) {
                                video.msRequestFullscreen();
                                return 'success_ms';
                            }
                            return 'no_api';
                        })();
                    ");
                    var result = fsResult?.ToString() ?? "";
                    log?.LogInformation("[VideoJS] Strategy 1 result: {Result}", result);
                    if (result.StartsWith("success"))
                    {
                        fsOk = true;
                        fsStrategy = "native_video_" + result;
                    }
                }
                catch (Exception ex) { log?.LogWarning(ex, "[VideoJS] Strategy 1 failed with exception"); }

                // Strategy 2: Fullscreen the document body (fills screen but keeps page layout)
                if (!fsOk)
                {
                    try
                    {
                        log?.LogInformation("[VideoJS] Strategy 2: Trying document.body.requestFullscreen()");
                        ((IJavaScriptExecutor)driver).ExecuteScript(@"
                            (function() {
                                var body = document.body || document.documentElement;
                                if (body.requestFullscreen) {
                                    body.requestFullscreen();
                                } else if (body.webkitRequestFullscreen) {
                                    body.webkitRequestFullscreen();
                                } else if (body.msRequestFullscreen) {
                                    body.msRequestFullscreen();
                                }
                            })();
                        ");
                        fsOk = true;
                        fsStrategy = "document_body";
                        log?.LogInformation("[VideoJS] Strategy 2 executed (document body fullscreen)");
                    }
                    catch (Exception ex) { log?.LogWarning(ex, "[VideoJS] Strategy 2 failed with exception"); }
                }

                // Strategy 3: Try the site's fullscreen button
                if (!fsOk)
                {
                    log?.LogInformation("[VideoJS] Strategy 3: Looking for fullscreen button");
                    try { new Actions(driver).MoveToElement(container).Perform(); Thread.Sleep(200); } catch { }
                    try
                    {
                        var waitFs = new WebDriverWait(new SystemClock(), driver, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(200));
                        var fsBtn = waitFs.Until(d =>
                        {
                            try { return d.FindElement(By.CssSelector("button.vjs-fullscreen-control, button[title*='Full'], button[aria-label*='Full']")); } catch { return null; }
                        });
                        if (fsBtn != null)
                        {
                            log?.LogInformation("[VideoJS] Strategy 3: Found fullscreen button, clicking");
                            try { ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView({block:'center',inline:'center'});", fsBtn); } catch { }
                            fsBtn.Click();
                            fsOk = true;
                            fsStrategy = "fullscreen_button";
                        }
                        else
                        {
                            log?.LogWarning("[VideoJS] Strategy 3: Fullscreen button not found");
                        }
                    }
                    catch (Exception ex) { log?.LogWarning(ex, "[VideoJS] Strategy 3 failed with exception"); }
                }

                // Strategy 4: Try keyboard 'f' shortcut
                if (!fsOk)
                {
                    try
                    {
                        log?.LogInformation("[VideoJS] Strategy 4: Trying keyboard 'f' shortcut");
                        ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].focus();", container);
                        Thread.Sleep(100);
                        container.Click();
                        Thread.Sleep(100);
                        container.SendKeys("f");
                        fsOk = true;
                        fsStrategy = "keyboard_f";
                        log?.LogInformation("[VideoJS] Strategy 4 executed (keyboard 'f')");
                    }
                    catch (Exception ex) { log?.LogWarning(ex, "[VideoJS] Strategy 4 failed with exception"); }
                }

                // Strategy 5: Double-click as last resort
                if (!fsOk)
                {
                    try
                    {
                        log?.LogInformation("[VideoJS] Strategy 5: Trying double-click");
                        new Actions(driver).MoveToElement(container).DoubleClick().Perform();
                        fsOk = true;
                        fsStrategy = "double_click";
                        log?.LogInformation("[VideoJS] Strategy 5 executed (double-click)");
                    }
                    catch (Exception ex) { log?.LogWarning(ex, "[VideoJS] Strategy 5 failed with exception"); }
                }

                // Wait briefly to confirm fullscreen state
                bool confirmedFullscreen = false;
                try
                {
                    log?.LogDebug("[VideoJS] Waiting to confirm fullscreen state");
                    var waitFsConfirm = new WebDriverWait(new SystemClock(), driver, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(200));
                    waitFsConfirm.Until(d =>
                    {
                        try { return (bool)(((IJavaScriptExecutor)d).ExecuteScript("return !!document.fullscreenElement") ?? false); } catch { return true; }
                    });
                    confirmedFullscreen = true;
                }
                catch { }
                
                // Check final fullscreen state
                try
                {
                    var finalFsState = ((IJavaScriptExecutor)driver).ExecuteScript("return !!document.fullscreenElement");
                    confirmedFullscreen = finalFsState is bool b && b;
                }
                catch { }
                
                log?.LogInformation("[VideoJS] Fullscreen sequence complete: strategy={Strategy}, confirmed={Confirmed}", fsStrategy, confirmedFullscreen);

                // Unmute and set volume
                try
                {
                    log?.LogDebug("[VideoJS] Setting volume and unmuting");
                    ((IJavaScriptExecutor)driver).ExecuteScript("(function(){Array.from(document.querySelectorAll('video')).forEach(v=>{try{v.muted=false;v.volume=1.0;v.play().catch(()=>{});}catch(e){}})})()");
                }
                catch { }
                
                log?.LogInformation("[VideoJS] PlayAndFullscreenVideoJs completed successfully");
                return true;
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "[VideoJS] PlayAndFullscreenVideoJs failed with exception");
                return false;
            }
        }
    }
}
