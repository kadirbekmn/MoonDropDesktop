using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WF = System.Windows.Forms;                 // FolderBrowserDialog için alias
using Path = System.IO.Path;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MoonDropDesktop
{
    public partial class MainWindow : Window
    {
        private readonly string _configPath;
        private AppConfig _cfg = new();
        private CancellationTokenSource? _cts;
        private WebApplication? _webApp;

        public MainWindow()
        {
            InitializeComponent(); // ← BUNUN üretilmesi için XAML eşleşmeleri yukarıdaki gibi olmalı
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App.config.json");
            LoadConfig();
        }

        void LoadConfig()
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                _cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                txtPath.Text = _cfg.BaseDir ?? "";
                if (!string.IsNullOrEmpty(_cfg.ApiKey)) txtApiKey.Password = _cfg.ApiKey!;
            }
        }

        void SaveConfig()
        {
            _cfg.BaseDir = txtPath.Text.Trim();
            _cfg.ApiKey  = string.IsNullOrWhiteSpace(txtApiKey.Password) ? null : txtApiKey.Password;
            var json = JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }

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
                System.Windows.MessageBox.Show("Geçerli bir klasör seçin.");
                return;
            }
            SaveConfig();
            if (_webApp != null)
            {
                System.Windows.MessageBox.Show("Sunucu zaten çalışıyor.");
                return;
            }
            await StartApiAsync();
        }

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            await StopApiAsync();
        }

        async Task StartApiAsync()
        {
            lblStatus.Text = "Durum: Başlatılıyor...";
            _cts = new CancellationTokenSource();

            var builder = WebApplication.CreateBuilder();

            // Kestrel dinleyici (UseUrls yerine)
var app = builder.Build();

// Dinlenecek adresi burada ekliyoruz (UseUrls/ConfigureKestrel yerine)
app.Urls.Add(_cfg.Url);   // örn: http://0.0.0.0:5005


            builder.Services.AddSingleton(_cfg);
            builder.Services.Configure<FormOptions>(o =>
            {
                o.MultipartBodyLengthLimit = 200 * 1024 * 1024; // toplam 200MB
            });


            // Basit CORS
            app.Use(async (ctx, next) =>
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Api-Key";
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
                var type = (form["type"].ToString() ?? "").Trim().ToLower(); // onarim|teslim

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

                return Results.Json(new { ok = true, plate, type, count = saved.Count, saved });
            });

            _webApp = app;
            _ = app.RunAsync(_cts.Token);

            lblStatus.Text = $"Durum: Çalışıyor → {_cfg.Url}";
            System.Windows.MessageBox.Show(
                $"Sunucu çalışıyor.\n\nHealth: {_cfg.Url}/api/health\nUpload: {_cfg.Url}/api/upload");
        }

        async Task StopApiAsync()
        {
            try
            {
                if (_cts != null && _webApp != null)
                {
                    _cts.Cancel();
                    await Task.Delay(200);
                }
            }
            catch { }
            finally
            {
                _webApp = null;
                _cts = null;
                lblStatus.Text = "Durum: Durduruldu";
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _ = StopApiAsync();
        }
    }

    public class AppConfig
    {
        public string? BaseDir { get; set; } = "";
        public string Url { get; set; } = "http://0.0.0.0:5005";
        public string? ApiKey { get; set; } = null;
    }
}
