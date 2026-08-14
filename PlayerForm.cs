using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using NAudio.Wave;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Bop;

public class PlayerForm : Form
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilter(uint msg, uint flags);

    private const int HOTKEY_ID = 9000;
    private const int WM_HOTKEY = 0x0312;
    private const uint VK_MEDIA_PLAY_PAUSE = 0xCD;
    private const uint VK_F8 = 0x77;
    public static Icon? GetEmbeddedIcon(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"Bop.{resourceName}");
        return stream != null ? new Icon(stream) : null;
    }

    private readonly Action<float> _onVolumeChanged;
    private readonly Action<double> _onSeekRequested;
    private readonly Action _onPlayPauseToggled;
    private readonly Action _onStopRequested;
    private WaveOutEvent? _outputDevice;
    private MediaFoundationReader? _audioFile;
    private string _songTitle = "Title of the video";
    private string _artistName = "YouTube channel";
    private bool _isUserSeeking = false;
    private bool _isUserVoluming = false;
    private double _currentProgress = 0.0;
    private float _currentVolume = 0.5f;
    private Image? _coverImage = null;
    private Bitmap? _blurredBanner = null;
    private Bitmap? _sharpBanner = null;
    private static readonly HttpClient _httpClient = new();
    private bool _isDarkMode = false;
    private bool _isHoveringBanner = false; // Suivi du survol de la miniature

    // --- Palette ---
    private Color _bgColor;
    private Color _primaryTextColor;
    private Color _secondaryTextColor;
    private Color _trackBgColor;
    private Color _coverBgColor;
    private Color _coverBgColor2;
    private readonly Color _accentColor = Color.FromArgb(151, 125, 255);

    private const int WM_APPCOMMAND = 0x0319;
    private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;

    // --- Zones cliquables ---
    private const int BannerHeight = 190;
    private Rectangle _bannerRect;
    private Rectangle _playBtnBounds;
    private Rectangle _prevBtnBounds;
    private Rectangle _nextBtnBounds;
    private Rectangle _closeBtnBounds;
    private Rectangle _seekBarBounds;
    private Rectangle _volumeBarBounds;

    // --- Survol (hover) ---
    private enum HoverTarget { None, Play, Prev, Next, Close }
    private HoverTarget _hoveredButton = HoverTarget.None;

    private readonly System.Windows.Forms.Timer _updateTimer;

    public PlayerForm(
        Action<float> onVolumeChanged,
        Action<double> onSeekRequested,
        Action onPlayPauseToggled,
        Action onStopRequested)
    {
        _onVolumeChanged = onVolumeChanged;
        _onSeekRequested = onSeekRequested;
        _onPlayPauseToggled = onPlayPauseToggled;
        _onStopRequested = onStopRequested;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(340, 350);
        TopMost = true;
        DoubleBuffered = true;

        var primaryScreen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        Location = new Point(primaryScreen.Right - Width - 30, primaryScreen.Bottom - Height - 30);

        _bannerRect = new Rectangle(0, 0, Width, BannerHeight);
        _closeBtnBounds = new Rectangle(Width - 40, 14, 26, 26);

        _playBtnBounds = new Rectangle((Width - 56) / 2, 118, 56, 56);
        _prevBtnBounds = new Rectangle(_playBtnBounds.X - 40 - 14, 126, 40, 40);
        _nextBtnBounds = new Rectangle(_playBtnBounds.Right + 14, 126, 40, 40);

        _seekBarBounds = new Rectangle(25, 200, Width - 50, 14);
        _volumeBarBounds = new Rectangle(65, 300, Width - 130, 14);

        ApplyRoundedRegion(24);
        UpdateThemeColors();
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;

        _updateTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _updateTimer.Tick += UpdateProgress;

        MouseDown += PlayerForm_MouseDown;
        MouseUp += PlayerForm_MouseUp;
        MouseMove += PlayerForm_MouseMove;
        MouseLeave += PlayerForm_MouseLeave;

        var icon = GetEmbeddedIcon("gmalalatete.ico");
        this.Icon = icon ?? SystemIcons.Application;
        //RegisterHotKey(this.Handle, HOTKEY_ID, 0x0000, VK_MEDIA_PLAY_PAUSE);
        //ChangeWindowMessageFilter(0x0312 /* WM_HOTKEY */, 1 /* MSGFLT_ADD */);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Autorise le message Hotkey même si l'application tourne avec des privilèges différents
        ChangeWindowMessageFilter(WM_HOTKEY, 1 /* MSGFLT_ADD */);

        // Essaie d'enregistrer la touche média (Fn+F8 sur la plupart des PC portable)
        bool registered = RegisterHotKey(this.Handle, HOTKEY_ID, 0x0000, VK_MEDIA_PLAY_PAUSE);

        // Si la touche Média native est déjà bloquée par Spotify/Chrome, on écoute Ctrl + F8 en secours
        if (!registered)
        {
            const uint MOD_CONTROL = 0x0002;
            RegisterHotKey(this.Handle, HOTKEY_ID, MOD_CONTROL, VK_F8);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
        {
            _onPlayPauseToggled();
            Invalidate();
            return;
        }
        
        if (m.Msg == WM_APPCOMMAND)
        {
            int cmd = (int)((m.LParam.ToInt64() >> 16) & 0xFFFF);
            if (cmd == APPCOMMAND_MEDIA_PLAY_PAUSE)
            {
                _onPlayPauseToggled();
                Invalidate();
                return;
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(this.Handle, HOTKEY_ID);
        base.OnHandleDestroyed(e);
    }

    private bool IsWindowsInDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var registryValue = key?.GetValue("AppsUseLightTheme");
            if (registryValue is int value)
            {
                return value == 0;
            }
        }
        catch { }
        return false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Nettoyage du raccourci global
        UnregisterHotKey(this.Handle, HOTKEY_ID);

        // Nettoyage des événements système
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;

        // Libération de la mémoire des images
        _coverImage?.Dispose();
        _coverImage = null;
        _blurredBanner?.Dispose();
        _blurredBanner = null;
        _sharpBanner?.Dispose();
        _sharpBanner = null;

        // Si l'utilisateur clique sur la croix, masque la fenêtre au lieu de la fermer
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    private void UpdateThemeColors()
    {
        _isDarkMode = IsWindowsInDarkMode();

        if (_isDarkMode)
        {
            _bgColor = Color.FromArgb(24, 24, 28);
            _primaryTextColor = Color.FromArgb(248, 248, 252);
            _secondaryTextColor = Color.FromArgb(165, 165, 178);
            _trackBgColor = Color.FromArgb(58, 58, 68);
            _coverBgColor = Color.FromArgb(94, 74, 165);
            _coverBgColor2 = Color.FromArgb(58, 44, 110);
        }
        else
        {
            _bgColor = Color.FromArgb(250, 250, 252);
            _primaryTextColor = Color.FromArgb(30, 30, 38);
            _secondaryTextColor = Color.FromArgb(115, 115, 128);
            _trackBgColor = Color.FromArgb(222, 222, 228);
            _coverBgColor = Color.FromArgb(196, 178, 255);
            _coverBgColor2 = Color.FromArgb(150, 130, 235);
        }

        BackColor = _bgColor;
        Invalidate();
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
        {
            UpdateThemeColors();
        }
    }

    private void ApplyRoundedRegion(int radius)
    {
        using GraphicsPath path = GetRoundedRectPath(new Rectangle(0, 0, Width, Height), radius);
        Region = new Region(path);
    }

    public void BindMedia(
        string title,
        string channelName = "YouTube Channel",
        WaveOutEvent? outputDevice = null,
        MediaFoundationReader? audioFile = null,
        float currentVolume = 0.5f,
        string? thumbnailUrl = null)
    {
        _songTitle = title;
        _artistName = channelName;
        _outputDevice = outputDevice;
        _audioFile = audioFile;
        _currentVolume = currentVolume;

        _coverImage?.Dispose();
        _coverImage = null;
        _blurredBanner?.Dispose();
        _blurredBanner = null;
        _sharpBanner?.Dispose();
        _sharpBanner = null;

        if (!string.IsNullOrEmpty(thumbnailUrl))
        {
            _ = LoadThumbnailAsync(thumbnailUrl);
        }

        _updateTimer.Start();
        if (!Visible) Show();
        BringToFront();
        Invalidate();
    }

    private async Task LoadThumbnailAsync(string url)
    {
        try
        {
            byte[] bytes = await _httpClient.GetByteArrayAsync(url);
            using MemoryStream ms = new MemoryStream(bytes);
            Image downloadedImg = Image.FromStream(ms);

            _coverImage = (Image)downloadedImg.Clone();
            _blurredBanner?.Dispose();
            _blurredBanner = CreateBlurredBanner(_coverImage, _bannerRect.Width, _bannerRect.Height);
            _sharpBanner?.Dispose();
            _sharpBanner = CreateSharpBanner(_coverImage, _bannerRect.Width, _bannerRect.Height);

            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(new Action(Invalidate));
            }
        }
        catch
        {
            _coverImage = null;
            _blurredBanner = null;
            _sharpBanner = null;
        }
    }

    private Bitmap CreateSharpBanner(Image source, int width, int height)
    {
        float imgRatio = (float)source.Width / source.Height;
        float rectRatio = (float)width / height;
        Rectangle srcRect;
        if (imgRatio > rectRatio)
        {
            int cropWidth = (int)(source.Height * rectRatio);
            srcRect = new Rectangle((source.Width - cropWidth) / 2, 0, cropWidth, source.Height);
        }
        else
        {
            int cropHeight = (int)(source.Width / rectRatio);
            srcRect = new Rectangle(0, (source.Height - cropHeight) / 2, source.Width, cropHeight);
        }

        var result = new Bitmap(width, height);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, width, height), srcRect, GraphicsUnit.Pixel);
        }
        return result;
    }

    private Bitmap CreateBlurredBanner(Image source, int width, int height, int blurFactor = 14)
    {
        int smallW = Math.Max(1, width / blurFactor);
        int smallH = Math.Max(1, height / blurFactor);

        float imgRatio = (float)source.Width / source.Height;
        float rectRatio = (float)width / height;
        Rectangle srcRect;
        if (imgRatio > rectRatio)
        {
            int cropWidth = (int)(source.Height * rectRatio);
            srcRect = new Rectangle((source.Width - cropWidth) / 2, 0, cropWidth, source.Height);
        }
        else
        {
            int cropHeight = (int)(source.Width / rectRatio);
            srcRect = new Rectangle(0, (source.Height - cropHeight) / 2, source.Width, cropHeight);
        }

        using var small = new Bitmap(smallW, smallH);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, smallW, smallH), srcRect, GraphicsUnit.Pixel);
        }

        var result = new Bitmap(width, height);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImage(small, 0, 0, width, height);

            using var dim = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
            g.FillRectangle(dim, 0, 0, width, height);
        }
        return result;
    }

    public void SetLoadingState(string statusText)
    {
        _songTitle = statusText;
        _artistName = "Loading...";
        _currentProgress = 0;

        _coverImage?.Dispose();
        _coverImage = null;
        _blurredBanner?.Dispose();
        _blurredBanner = null;
        _sharpBanner?.Dispose();
        _sharpBanner = null;

        if (!Visible) Show();
        BringToFront();
        Invalidate();
    }

    private bool _isDragging = false;
    private Point _dragCursorPoint;
    private Point _dragFormPoint;

    private void PlayerForm_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_closeBtnBounds.Contains(e.Location))
            {
                Hide();
                return;
            }

            // Les boutons audio réagissent uniquement si la miniature est survolée
            if (_isHoveringBanner)
            {
                if (_playBtnBounds.Contains(e.Location))
                {
                    _onPlayPauseToggled();
                    Invalidate();
                    return;
                }

                if (_prevBtnBounds.Contains(e.Location))
                {
                    SeekRelative(-5);
                    return;
                }

                if (_nextBtnBounds.Contains(e.Location))
                {
                    SeekRelative(5);
                    return;
                }
            }

            if (_seekBarBounds.Contains(e.Location))
            {
                _isUserSeeking = true;
                HandleSeek(e.X);
                return;
            }

            if (_volumeBarBounds.Contains(e.Location))
            {
                _isUserVoluming = true;
                HandleVolume(e.X);
                return;
            }

            _isDragging = true;
            _dragCursorPoint = Cursor.Position;
            _dragFormPoint = Location;
        }
    }

    private void PlayerForm_MouseMove(object? sender, MouseEventArgs e)
    {
        bool wasHoveringBanner = _isHoveringBanner;
        _isHoveringBanner = _bannerRect.Contains(e.Location);

        if (_isDragging)
        {
            Point dif = Point.Subtract(Cursor.Position, new Size(_dragCursorPoint));
            Location = Point.Add(_dragFormPoint, new Size(dif));
        }
        else if (_isUserSeeking)
        {
            HandleSeek(e.X);
        }
        else if (_isUserVoluming)
        {
            HandleVolume(e.X);
        }

        var newHover = HoverTarget.None;
        if (_closeBtnBounds.Contains(e.Location)) newHover = HoverTarget.Close;
        else if (_isHoveringBanner)
        {
            if (_playBtnBounds.Contains(e.Location)) newHover = HoverTarget.Play;
            else if (_prevBtnBounds.Contains(e.Location)) newHover = HoverTarget.Prev;
            else if (_nextBtnBounds.Contains(e.Location)) newHover = HoverTarget.Next;
        }

        if (newHover != _hoveredButton || wasHoveringBanner != _isHoveringBanner)
        {
            _hoveredButton = newHover;
            Cursor = newHover != HoverTarget.None ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    private void PlayerForm_MouseLeave(object? sender, EventArgs e)
    {
        if (_isHoveringBanner || _hoveredButton != HoverTarget.None)
        {
            _isHoveringBanner = false;
            _hoveredButton = HoverTarget.None;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    private void PlayerForm_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = false;
            if (_isUserSeeking)
            {
                _isUserSeeking = false;
                _onSeekRequested(_currentProgress);
            }
            if (_isUserVoluming)
            {
                _isUserVoluming = false;
            }
        }
    }

    private void HandleSeek(int mouseX)
    {
        int relativeX = Math.Clamp(mouseX - _seekBarBounds.X, 0, _seekBarBounds.Width);
        _currentProgress = (double)relativeX / _seekBarBounds.Width;
        Invalidate();
    }

    private void HandleVolume(int mouseX)
    {
        int relativeX = Math.Clamp(mouseX - _volumeBarBounds.X, 0, _volumeBarBounds.Width);
        _currentVolume = (float)relativeX / _volumeBarBounds.Width;
        _onVolumeChanged(_currentVolume);
        Invalidate();
    }

    private void SeekRelative(double seconds)
    {
        if (_audioFile == null) return;
        var newTime = _audioFile.CurrentTime.Add(TimeSpan.FromSeconds(seconds));
        if (newTime < TimeSpan.Zero) newTime = TimeSpan.Zero;
        if (newTime > _audioFile.TotalTime) newTime = _audioFile.TotalTime;
        _audioFile.CurrentTime = newTime;
    }

    private void UpdateProgress(object? sender, EventArgs e)
    {
        if (_audioFile != null && !_isUserSeeking && _audioFile.TotalTime.TotalSeconds > 0)
        {
            _currentProgress = _audioFile.CurrentTime.TotalSeconds / _audioFile.TotalTime.TotalSeconds;
        }
        Invalidate();
    }

    // --- RENDU GRAPHIQUE ---
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        g.Clear(_bgColor);

        DrawBanner(g);
        DrawBannerControls(g);
        DrawProgressRow(g);
        DrawTitleBlock(g);
        DrawVolumeRow(g);
    }

    private void DrawBanner(Graphics g)
    {
        using GraphicsPath bannerPath = GetTopRoundedRectPath(_bannerRect, 24);
        GraphicsState state = g.Save();
        g.SetClip(bannerPath);

        // Si survolé : affiche le bandeau flou. Sinon : affiche le bandeau net.
        if (_isHoveringBanner && _blurredBanner != null)
        {
            g.DrawImage(_blurredBanner, _bannerRect);
        }
        else if (!_isHoveringBanner && _sharpBanner != null)
        {
            g.DrawImage(_sharpBanner, _bannerRect);
        }
        else
        {
            using var placeholder = new LinearGradientBrush(
                _bannerRect, _coverBgColor, _coverBgColor2, LinearGradientMode.ForwardDiagonal);
            g.FillRectangle(placeholder, _bannerRect);
            DrawMusicNote(g, new Point(_bannerRect.Width / 2, _bannerRect.Height / 2 - 20));
        }

        // Voile d'ombrage lors du survol pour améliorer le contraste des boutons
        if (_isHoveringBanner)
        {
            using var overlay = new LinearGradientBrush(
                _bannerRect,
                Color.FromArgb(10, 0, 0, 0),
                Color.FromArgb(150, 0, 0, 0),
                LinearGradientMode.Vertical);
            var blend = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(0, 0, 0, 0),
                    Color.FromArgb(20, 0, 0, 0),
                    Color.FromArgb(170, 0, 0, 0)
                },
                Positions = new[] { 0f, 0.45f, 1f }
            };
            overlay.InterpolationColors = blend;
            g.FillRectangle(overlay, _bannerRect);
        }

        g.Restore(state);
    }

    private void DrawBannerControls(Graphics g)
    {
        // Croix de fermeture (toujours affichée)
        bool closeHover = _hoveredButton == HoverTarget.Close;
        using (SolidBrush closeBg = new SolidBrush(Color.FromArgb(closeHover ? 90 : 55, 0, 0, 0)))
        {
            g.FillEllipse(closeBg, _closeBtnBounds);
        }
        using (Pen closePen = new Pen(Color.White, 1.6f))
        {
            int pad = 8;
            g.DrawLine(closePen, _closeBtnBounds.X + pad, _closeBtnBounds.Y + pad, _closeBtnBounds.Right - pad, _closeBtnBounds.Bottom - pad);
            g.DrawLine(closePen, _closeBtnBounds.Right - pad, _closeBtnBounds.Y + pad, _closeBtnBounds.X + pad, _closeBtnBounds.Bottom - pad);
        }

        // Afficher les boutons Play / Avancer / Reculer SEULEMENT en cas de survol de la miniature
        if (_isHoveringBanner)
        {
            // Précédent (-5s)
            DrawGlassCircle(g, _prevBtnBounds, _hoveredButton == HoverTarget.Prev);
            DrawCircularArrow(g, _prevBtnBounds, Color.White, isForward: false);

            // Suivant (+5s)
            DrawGlassCircle(g, _nextBtnBounds, _hoveredButton == HoverTarget.Next);
            DrawCircularArrow(g, _nextBtnBounds, Color.White, isForward: true);

            // Lecture / Pause
            bool playHover = _hoveredButton == HoverTarget.Play;
            int growth = playHover ? 2 : 0;
            Rectangle playCircle = Rectangle.Inflate(_playBtnBounds, growth, growth);
            using (SolidBrush playBg = new SolidBrush(Color.White))
            {
                g.FillEllipse(playBg, playCircle);
            }

            bool isPlaying = _outputDevice != null && _outputDevice.PlaybackState == PlaybackState.Playing;
            Color iconColor = Color.FromArgb(20, 20, 24);
            if (isPlaying)
                DrawPauseIcon(g, playCircle, iconColor);
            else
                DrawPlayIcon(g, playCircle, iconColor);
        }
    }

    private void DrawGlassCircle(Graphics g, Rectangle r, bool hovered)
    {
        using SolidBrush b = new SolidBrush(Color.FromArgb(hovered ? 55 : 28, 255, 255, 255));
        g.FillEllipse(b, r);
    }

    private void DrawProgressRow(Graphics g)
    {
        using (Pen bgTrackPen = new Pen(_trackBgColor, 3))
        using (Pen fillTrackPen = new Pen(_accentColor, 3))
        {
            int yTrack = _seekBarBounds.Y + 5;
            g.DrawLine(bgTrackPen, _seekBarBounds.X, yTrack, _seekBarBounds.Right, yTrack);

            int progressWidth = (int)(_seekBarBounds.Width * Math.Clamp(_currentProgress, 0.0, 1.0));
            if (progressWidth > 0)
            {
                g.DrawLine(fillTrackPen, _seekBarBounds.X, yTrack, _seekBarBounds.X + progressWidth, yTrack);
            }

            using SolidBrush thumbBrush = new SolidBrush(_accentColor);
            g.FillEllipse(thumbBrush, _seekBarBounds.X + progressWidth - 5, yTrack - 5, 10, 10);
        }

        TimeSpan currentTS = _audioFile != null ? _audioFile.CurrentTime : TimeSpan.Zero;
        TimeSpan totalTS = _audioFile != null ? _audioFile.TotalTime : TimeSpan.Zero;

        using Font timeFont = new Font("Segoe UI", 8f, FontStyle.Regular);
        using SolidBrush timeBrush = new SolidBrush(_secondaryTextColor);
        int yTime = _seekBarBounds.Bottom - 2;
        g.DrawString($"{currentTS:mm\\:ss}", timeFont, timeBrush, _seekBarBounds.X, yTime);
        StringFormat sfRight = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString($"{totalTS:mm\\:ss}", timeFont, timeBrush, _seekBarBounds.Right, yTime, sfRight);
    }

    private void DrawTitleBlock(Graphics g)
    {
        using Font titleFont = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
        using Font artistFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        using SolidBrush titleBrush = new SolidBrush(_primaryTextColor);
        using SolidBrush artistBrush = new SolidBrush(_secondaryTextColor);

        StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(_songTitle, titleFont, titleBrush, new RectangleF(15, 236, Width - 30, 24), sf);
        g.DrawString(_artistName, artistFont, artistBrush, new RectangleF(15, 260, Width - 30, 18), sf);
    }

    private void DrawVolumeRow(Graphics g)
    {
        DrawSpeakerIcon(g, new Point(_volumeBarBounds.X - 22, _volumeBarBounds.Y - 2), _secondaryTextColor, isHigh: false);

        using (Pen bgVolPen = new Pen(_trackBgColor, 3))
        using (Pen fillVolPen = new Pen(_accentColor, 3))
        {
            int yVol = _volumeBarBounds.Y + 5;
            g.DrawLine(bgVolPen, _volumeBarBounds.X, yVol, _volumeBarBounds.Right, yVol);

            int volWidth = (int)(_volumeBarBounds.Width * Math.Clamp(_currentVolume, 0.0f, 1.0f));
            if (volWidth > 0)
            {
                g.DrawLine(fillVolPen, _volumeBarBounds.X, yVol, _volumeBarBounds.X + volWidth, yVol);
            }

            using SolidBrush volThumb = new SolidBrush(_accentColor);
            g.FillEllipse(volThumb, _volumeBarBounds.X + volWidth - 7, yVol - 7, 14, 14);
        }

        DrawSpeakerIcon(g, new Point(_volumeBarBounds.Right + 8, _volumeBarBounds.Y - 2), _secondaryTextColor, isHigh: true);
    }

    // --- ICÔNES ---
    private void DrawMusicNote(Graphics g, Point center)
    {
        using SolidBrush b = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
        using Pen p = new Pen(Color.FromArgb(230, 255, 255, 255), 3);
        g.FillEllipse(b, center.X - 18, center.Y + 6, 12, 8);
        g.DrawLine(p, center.X - 8, center.Y + 10, center.X - 8, center.Y - 14);
        g.DrawLine(p, center.X - 8, center.Y - 14, center.X + 8, center.Y - 20);
    }

    private void DrawPlayIcon(Graphics g, Rectangle r, Color color)
    {
        using SolidBrush b = new SolidBrush(color);
        PointF[] p = {
            new PointF(r.X + r.Width * 0.40f, r.Y + r.Height * 0.28f),
            new PointF(r.X + r.Width * 0.40f, r.Y + r.Height * 0.72f),
            new PointF(r.X + r.Width * 0.74f, r.Y + r.Height * 0.5f)
        };
        g.FillPolygon(b, p);
    }

    private void DrawPauseIcon(Graphics g, Rectangle r, Color color)
    {
        using SolidBrush b = new SolidBrush(color);
        float barW = r.Width * 0.11f;
        float barH = r.Height * 0.4f;
        g.FillRectangle(b, r.X + r.Width * 0.34f, r.Y + (r.Height - barH) / 2, barW, barH);
        g.FillRectangle(b, r.X + r.Width * 0.55f, r.Y + (r.Height - barH) / 2, barW, barH);
    }

    private void DrawCircularArrow(Graphics g, Rectangle r, Color color, bool isForward)
    {
        int margin = 9;
        Rectangle arcRect = new Rectangle(r.X + margin, r.Y + margin, r.Width - margin * 2, r.Height - margin * 2);

        if (arcRect.Width <= 0 || arcRect.Height <= 0) return;

        using Pen p = new Pen(color, 2.0f);
        using CustomLineCap arrowCap = new AdjustableArrowCap(3.2f, 3.6f, true);
        p.CustomEndCap = arrowCap;

        if (isForward)
        {
            g.DrawArc(p, arcRect, 30, 290);
        }
        else
        {
            g.DrawArc(p, arcRect, 150, -290);
        }
    }

    private void DrawSpeakerIcon(Graphics g, Point pt, Color color, bool isHigh)
    {
        using SolidBrush b = new SolidBrush(color);
        using Pen p = new Pen(color, 2);
        g.FillRectangle(b, pt.X, pt.Y + 4, 3, 6);
        PointF[] pPolygon = { new PointF(pt.X + 3, pt.Y + 4), new PointF(pt.X + 7, pt.Y + 1), new PointF(pt.X + 7, pt.Y + 13), new PointF(pt.X + 3, pt.Y + 10) };
        g.FillPolygon(b, pPolygon);

        if (isHigh)
        {
            g.DrawArc(p, pt.X + 7, pt.Y + 2, 6, 10, -60, 120);
            g.DrawArc(p, pt.X + 9, pt.Y, 9, 14, -60, 120);
        }
        else
        {
            g.DrawArc(p, pt.X + 7, pt.Y + 3, 5, 8, -60, 120);
        }
    }

    private GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int diameter = radius * 2;

        if (rect.Width < diameter || rect.Height < diameter)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private GraphicsPath GetTopRoundedRectPath(Rectangle rect, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int diameter = radius * 2;

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddLine(rect.Right, rect.Y + radius, rect.Right, rect.Bottom);
        path.AddLine(rect.Right, rect.Bottom, rect.X, rect.Bottom);
        path.CloseFigure();
        return path;
    }
}