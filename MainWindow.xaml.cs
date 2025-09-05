using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WF = System.Windows.Forms;                 // WinForms alias (NotifyIcon / FolderBrowserDialog)
using Path = System.IO.Path;
using Microsoft.AspNetCore.Hosting; // ← BUNU EKLE


namespace MoonDropDesktop
{
    public partial class MainWindow : Window
    {
        private readonly string _configPath;
        private AppConfig _cfg = new();
        private CancellationTokenSource? _cts;
        private WebApplication? _webApp;

        // Tray icon
        private WF.NotifyIcon? _tray;
        private string? _lastOpenPath;

        public MainWindow()
        {
            InitializeComponent();

            // Ayar dosyası konumu
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MoonDropDesktop");
            Directory.CreateDirectory(appDataDir);
            _configPath = Path.Combine(appDataDir, "App.config.json");

            // Tray
            _tray = new WF.NotifyIcon
            {
                Visible = true,
                Text = "MoonDrop Desktop",
                Icon = System.Drawing.SystemIcons.Application
            };
            _tray.BalloonTipClicked += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(_lastOpenPath) && Directory.Exists(_lastOpenPath))
                {
                    try { Process.Start("explorer.exe", _lastOpenPath); } catch { }
                }
            };

            LoadConfig();
        }

        // --- Config yükle/kaydet -------------------------------------------------

        void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    _cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                    txtPath.Text = _cfg.BaseDir ?? "";
                    if (!string.IsNullOrEmpty(_cfg.ApiKey)) txtApiKey.Password = _cfg.ApiKey!;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ayarlar okunamadı:\n" + ex);
            }
        }

        void SaveConfig()
        {
            try
            {
                _cfg.BaseDir = txtPath.Text.Trim();
                _cfg.ApiKey  = string.IsNullOrWhiteSpace(txtApiKey.Password) ? null : txtApiKey.Password;

                var dir = Path.GetDirectoryName(_configPath)!;
                Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ayarlar kaydedilemedi:\n" + ex);
            }
        }

        // --- UI eventleri --------------------------------------------------------

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new WF.FolderBrowserDialog { Description = "Resimlerin kaydedileceği klasörü seçin" };
            if (dlg.ShowDialog() == WF.DialogResult.OK)
                txtPath.Text = dlg.SelectedPath;
        }

        private async void SaveAndStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtPath.Text) || !Directory.Exists(txtPath.Text))
            {
                MessageBox.Show("Geçerli bir klasör seçin.");
                return;
            }
            SaveConfig();
            if (_webApp != null)
            {
                MessageBox.Show("Sunucu zaten çalışıyor.");
                return;
            }
            await StartApiAsync();
        }

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            await StopApiAsync();
        }

        // --- API başlat/durdur ---------------------------------------------------

        async Task StartApiAsync()
        {
            try
            {
                lblStatus.Content = "Durum: Başlatılıyor...";
                _cts = new CancellationTokenSource();

                _cfg.Url = "http://127.0.0.1:8080";

                var builder = WebApplication.CreateBuilder();
                builder.WebHost
                    .UseUrls(_cfg.Url)
                    .ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 200 * 1024 * 1024; });

                builder.Services.AddSingleton(_cfg);
                // (CORS + endpointler zaten sende var)



                var app = builder.Build();

                // Basit CORS
                app.Use(async (ctx, next) =>
                {
                    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                    ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Api-Key";
                    ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
                    if (ctx.Request.Method == "OPTIONS")
                    {
                        ctx.Response.StatusCode = 204; return;
                    }
                    await next();
                });

                // Sağlık
                app.MapGet("/api/health", (AppConfig cfg) => Results.Json(new
                {
                    ok = true,
                    baseDir = cfg.BaseDir,
                    url = cfg.Url
                }));

                // Upload
                app.MapPost("/api/upload", async (HttpRequest req, AppConfig cfg) =>
                {
                    if (!string.IsNullOrEmpty(cfg.ApiKey))
                    {
                        if (!req.Headers.TryGetValue("X-Api-Key", out var key) || key != cfg.ApiKey)
                            return Results.Unauthorized();
                    }

                    if (!req.HasFormContentType) return Results.BadRequest("Form-Data bekleniyor.");
                    var form = await req.ReadFormAsync();
                    var plateRaw = (form["plate"].ToString() ?? "").Trim();
                    var type = (form["type"].ToString() ?? "").Trim().ToLower(); // onarim | teslim

                    if (string.IsNullOrWhiteSpace(plateRaw) || (type != "onarim" && type != "teslim"))
                        return Results.BadRequest("Eksik/Geçersiz alanlar (plate/type).");

                    if (string.IsNullOrWhiteSpace(cfg.BaseDir) || !Directory.Exists(cfg.BaseDir))
                        return Results.BadRequest("BaseDir ayarlı değil.");

                    var plate = Regex.Replace(plateRaw.ToUpperInvariant(), "[\\s\\-]", "");
                    var dest = Path.Combine(cfg.BaseDir!, plate, type);
                    Directory.CreateDirectory(dest);

                    var files = form.Files;
                    if (files == null || files.Count == 0)
                        return Results.BadRequest("En az bir resim gönderin.");

                    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "image/jpeg", "image/jpg", "image/png" };

                    var saved = new List<object>();
                    foreach (var file in files)
                    {
                        if (!allowed.Contains(file.ContentType))
                            return Results.BadRequest($"Sadece PNG/JPEG kabul edilir. Gönderilen: {file.ContentType}");

                        if (file.Length > 10 * 1024 * 1024)
                            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                        var ext = file.ContentType.Contains("png") ? ".png" : ".jpg";
                        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                        var rand = Path.GetRandomFileName().Replace(".", "");
                        var fileName = $"{stamp}_{rand}{ext}";
                        var full = Path.Combine(dest, fileName);

                        using var stream = File.Create(full);
                        await file.CopyToAsync(stream);

                        saved.Add(new { fileName, fullPath = full, size = file.Length });
                    }

                    try
                    {
                        _lastOpenPath = dest;
                        _tray?.ShowBalloonTip(
                            3000,
                            "MoonDrop",
                            $"{plate} - {type} : {saved.Count} dosya kaydedildi",
                            WF.ToolTipIcon.Info
                        );
                    }
                    catch { }

                    return Results.Json(new { ok = true, plate, type, count = saved.Count, saved });
                });

                _webApp = app;

                // *** ÖNEMLİ: Sunucuyu senkron başlat. Port bağlanmıyorsa burada exception alırız. ***
                await app.StartAsync(_cts.Token);

                // Arka planda kapanmayı beklesin (UI bloke olmasın)
                _ = Task.Run(async () =>
                {
                    try { await app.WaitForShutdownAsync(_cts.Token); } catch { }
                });

                lblStatus.Content = $"Durum: Çalışıyor → {_cfg.Url}";
                MessageBox.Show($"Sunucu çalışıyor.\n\nHealth: {_cfg.Url}/api/health\nUpload: {_cfg.Url}/api/upload");
            }
            catch (Exception ex)
            {
                lblStatus.Content = "Durum: Başlatılamadı";
                MessageBox.Show("Başlatma hatası:\n" + ex);
                await StopApiAsync();
            }
        }

        async Task StopApiAsync()
        {
            try
            {
                if (_webApp != null)
                {
                    try { await _webApp.StopAsync(); } catch { }
                }

                if (_cts != null)
                {
                    _cts.Cancel();
                    await Task.Delay(150);
                }
            }
            catch { }
            finally
            {
                _webApp = null;
                _cts = null;
                lblStatus.Content = "Durum: Durduruldu";
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _ = StopApiAsync();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }
    }

    // Config
        public class AppConfig
        {
            public string? BaseDir { get; set; } = "";
            public string Url { get; set; } = "http://127.0.0.1:8080";
            public string? ApiKey { get; set; } = null;
        }



}
