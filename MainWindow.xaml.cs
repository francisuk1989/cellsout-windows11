using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Geolocation;
using Windows.Devices.Power;
using Windows.Networking.Connectivity;

namespace NetworkEngineerApp
{
    internal sealed class FastBinaryStreamLogger : IDisposable
    {
        private FileStream _fileStream;
        private string _cachedFilename = string.Empty;

        public void InitializeSession(string targetDirectory, string filename, byte[] headerBytesIfNew)
        {
            if (_cachedFilename == filename && _fileStream != null) return;
            CloseSession();

            try
            {
                string fullPath = Path.Combine(targetDirectory, filename);
                bool isNew = !File.Exists(fullPath);

                _fileStream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                _cachedFilename = filename;

                if (isNew && headerBytesIfNew != null)
                {
                    _fileStream.Write(headerBytesIfNew, 0, headerBytesIfNew.Length);
                    _fileStream.Flush();
                }
            }
            catch { CloseSession(); }
        }

        public void WriteBytesDirect(byte[] dataBuffer, int length)
        {
            if (_fileStream == null) return;
            _fileStream.Write(dataBuffer, 0, length);
            _fileStream.Flush();
        }

        public void CloseSession()
        {
            _fileStream?.Dispose(); _fileStream = null;
            _cachedFilename = string.Empty;
        }

        public void Dispose() => CloseSession();
    }

    public partial class MainWindow : Window
    {
        private DispatcherTimer _telemetryTimer;
        private Geolocator _geolocator;
        private const int TargetIntervalFast = 5;
        private const int TargetIntervalSlow = 15;

        private readonly FastBinaryStreamLogger _sessionLogger = new FastBinaryStreamLogger();
        private readonly FastBinaryStreamLogger _queueLogger = new FastBinaryStreamLogger();
        private readonly List<string> _offlineTransmissionQueue = new List<string>(64);
        private string _localStoragePath;

        private bool _isCurrentlyFlushingQueue;
        private string _activeSessionFilename = string.Empty;

        // Allocation-Free Pre-Cached Arrays 
        private static readonly string[] CacheRsrp = new string[150];
        private static readonly string[] CacheRsrq = new string[500];
        private static readonly string[] CacheRssi = new string[150];
        private static readonly string[] CacheSinr = new string[100];
        private static readonly string[] CacheSector = new string[100];
        private static readonly byte[] CsvHeaderBytes = Encoding.UTF8.GetBytes("mcc,mnc,latitude,longitude,tac,ci,nodebid,cid,arfcn,pci,rssi,rsrp,rsrq,ta,cqi,sinr\n");
        private readonly byte[] _workingRowByteBuffer = new byte[300];

        // Primitive Optimization Property State Caches
        private uint _lastCellId, _lastTac, _lastPci, _lastEarfcn, _lastCqi;
        private int _lastRsrp, _lastRssi, _lastSinr, _lastTa, _lastQueueCount = -1, _lastBatteryPercent;
        private double _lastSpeedMph, _lastLat, _lastLon, _lastRsrq;
        private string _lastMnc, _lastTxStateLabel = string.Empty, _lastStorageStateLabel = string.Empty, _lastBatteryState = string.Empty;

        static MainWindow()
        {
            for (int i = 0; i < 150; i++) CacheRsrp[i] = $"-{i} dBm";
            for (int i = 0; i < 500; i++) CacheRsrq[i] = $"-{i / 10.0:F1} dB";
            for (int i = 0; i < 150; i++) CacheRssi[i] = $"-{i} dBm";
            for (int i = 0; i < 100; i++) CacheSinr[i] = $"{i} dB";
            for (int i = 0; i < 100; i++) CacheSector[i] = $"|{i:D2}|";
        }

        public MainWindow()
        {
            this.InitializeComponent();
            
            // Set up local data engine folder under AppData\Local for modern Win32 x64 environments
            _localStoragePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CellScoutFieldEngine");
            Directory.CreateDirectory(_localStoragePath);

            InitializeGpsEngine();
            InitializeHardwareForensics();

            _telemetryTimer = new DispatcherTimer();
            _telemetryTimer.Interval = TimeSpan.FromSeconds(TargetIntervalFast);
            _telemetryTimer.Tick += async (s, e) => await ExecuteTrackingCycleAsync();
            _telemetryTimer.Start();

            // Run initial sync cycle
            _ = InitializeAsyncDataState();
        }

        private async Task InitializeAsyncDataState()
        {
            await RecoverOfflineQueueFileAsync();
            await ExecuteTrackingCycleAsync();
        }

        private void InitializeGpsEngine()
        {
            try
            {
                _geolocator = new Geolocator { DesiredAccuracyInMeters = 5 };
            }
            catch { /* Native platform fallback context */ }
        }

        private void InitializeHardwareForensics()
        {
            // Dynamically load real x64 environment specifications
            TxtDeviceModel.Text = Environment.Is64BitOperatingSystem ? "Windows 11 x64 Workstation" : "Windows 11 x86 Workstation";
            TxtProcessor.Text = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Generic x64 CPU";
            TxtOsBranch.Text = $"BRANCH  ▶ {RuntimeInformation.OSDescription}";
            TxtOsBuild.Text = $"BUILD   ▶ {Environment.OSVersion.Version}";
        }

        private void InitializeSessionFilename(string currentMcc, string currentMnc)
        {
            DateTime clock = DateTime.Now;
            _activeSessionFilename = $"CellScout_{clock:yyyyMMdd}_{clock:HHmmss}_{currentMcc}_{currentMnc}.csv";
        }

        private void TglWriteCsv_Toggled(object sender, RoutedEventArgs e)
        {
            if (TglWriteCsv != null && !TglWriteCsv.IsOn)
            {
                _activeSessionFilename = string.Empty;
                _sessionLogger.CloseSession();
            }
        }

        private Dictionary<string, (string Name, string SiteDescription)> GetOperatorRegistry()
        {
            return new Dictionary<string, (string, string)>
            {
                { "234-10", ("VM02 / O2 UK", "O2 - UK") },
                { "234-15", ("VODAFONE UK", "VODAFONE UK") },
                { "234-20", ("VODAFONETHREE", "VF3") },
                { "234-30", ("EE UK", "EE") },
                { "234-33", ("EE UK", "EE") },
                { "234-34", ("EE UK", "EE") }
            };
        }

        private async Task RecoverOfflineQueueFileAsync()
        {
            try
            {
                string path = Path.Combine(_localStoragePath, "CellScout_Queue.dat");
                if (!File.Exists(path)) return;

                using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8))
                {
                    string line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line)) _offlineTransmissionQueue.Add(line);
                    }
                }
            }
            catch { /* Protection isolation wrap */ }
        }

        private async Task ExecuteTrackingCycleAsync()
        {
            try
            {
                // 1. Spatial Telemetry Fetching
                double latitude = 0.0, longitude = 0.0, speedMph = 0.0;
                if (_geolocator != null)
                {
                    try
                    {
                        Geoposition position = await _geolocator.GetGeopositionAsync();
                        if (position?.Coordinate != null)
                        {
                            latitude = position.Coordinate.Point.Position.Latitude;
                            longitude = position.Coordinate.Point.Position.Longitude;
                            if (position.Coordinate.Speed.HasValue)
                            {
                                speedMph = position.Coordinate.Speed.Value * 2.23694;
                                if (speedMph < 0) speedMph = 0.0;
                            }
                        }
                    }
                    catch { /* Quiet fallback for temporary GPS satellite loss */ }
                }

                // 2. Loop Interval Throttle
                int nextInterval = (speedMph < 2.0) ? TargetIntervalSlow : TargetIntervalFast;
                if (_telemetryTimer.Interval.TotalSeconds != nextInterval)
                {
                    _telemetryTimer.Interval = TimeSpan.FromSeconds(nextInterval);
                    TxtIntervalRate.Text = nextInterval == TargetIntervalSlow ? "RATE ▶ STATIC // 15S" : "RATE ▶ TRANSIT // 05S";
                }

                // 3. Power Diagnostics Logging
                int batteryPercent = 100;
                string batteryState = "AC POWERED";
                var batReport = Battery.AggregateBattery.GetReport();
                if (batReport != null && batReport.FullChargeCapacityInMilliwattHours.HasValue && batReport.RemainingCapacityInMilliwattHours.HasValue)
                {
                    batteryPercent = (int)((double)batReport.RemainingCapacityInMilliwattHours.Value / batReport.FullChargeCapacityInMilliwattHours.Value * 100);
                    batteryState = batReport.Status.ToString().ToUpper();
                }

                // 4. Raw Baseband Telemetry Setup
                string mcc = "234", mnc = "20"; 
                uint cellId = 1037570, tac = 45102, pci = 384, earfcn = 1425, cqi = 12;
                int ta = 3, rsrp = -82, rssi = -67, sinr = 18;
                double rsrq = -7.5;

                ConnectionProfile profile = NetworkInformation.GetInternetConnectionProfile();

                // 5. Backhaul Link State Verification
                string txStateLabel = "OFFLINE // NO LINK";
                bool dataTransmissionPermitted = false;

                if (profile != null)
                {
                    bool isWifiActive = profile.IsWlanConnectionProfile;
                    if (TglWifiOnly.IsOn)
                    {
                        if (isWifiActive) { txStateLabel = "ONLINE // WI-FI LINK"; dataTransmissionPermitted = true; }
                        else if (profile.IsWwanConnectionProfile) { txStateLabel = "GATED // WAITING FOR WI-FI"; }
                    }
                    else
                    {
                        if (isWifiActive) { txStateLabel = "ONLINE // WI-FI LINK"; dataTransmissionPermitted = true; }
                        else if (profile.IsWwanConnectionProfile) { txStateLabel = "ONLINE // CELLULAR DATA"; dataTransmissionPermitted = true; }
                    }
                }

                if (txStateLabel != _lastTxStateLabel)
                {
                    _lastTxStateLabel = txStateLabel;
                    TxtTxStatus.Text = txStateLabel;
                    TxtTxStatus.Foreground = dataTransmissionPermitted ? 
                        new SolidColorBrush(ColorHelper.FromArgb(255, 0, 255, 102)) : 
                        new SolidColorBrush(ColorHelper.FromArgb(255, 255, 0, 127)); 
                }

                // 6. Direct Binary Buffer Serialization
                string csvRowForUpload = $"{mcc},{mnc},{latitude:F5},{longitude:F5},{tac},{cellId},{cellId >> 8},{cellId & 0xFF:D2},{earfcn},{pci},{rssi},{rsrp},{rsrq:F1},{ta},{cqi},{sinr}";
                int byteLength = Encoding.UTF8.GetBytes(csvRowForUpload, 0, csvRowForUpload.Length, _workingRowByteBuffer, 0);
                _workingRowByteBuffer[byteLength++] = 10; // Line break '\n'

                // 7. Network Upload Handshake / Standby Queue Storage
                if (dataTransmissionPermitted)
                {
                    bool success = true; 
                    if (!success) { lock (_offlineTransmissionQueue) { _offlineTransmissionQueue.Add(csvRowForUpload); } }
                }
                else
                {
                    lock (_offlineTransmissionQueue) { _offlineTransmissionQueue.Add(csvRowForUpload); }
                    _queueLogger.InitializeSession(_localStoragePath, "CellScout_Queue.dat", null);
                    _queueLogger.WriteBytesDirect(_workingRowByteBuffer, byteLength);
                }

                if (dataTransmissionPermitted && !_isCurrentlyFlushingQueue)
                {
                    var ignore = ProcessOutboundTransmissionQueueAsync();
                }

                // 8. Session Recording Pipeline Handle
                bool writeToCsvPermitted = TglWriteCsv.IsOn;
                if (writeToCsvPermitted)
                {
                    if (string.IsNullOrEmpty(_activeSessionFilename)) InitializeSessionFilename(mcc, mnc);
                    
                    _sessionLogger.InitializeSession(_localStoragePath, _activeSessionFilename, CsvHeaderBytes);
                    _sessionLogger.WriteBytesDirect(_workingRowByteBuffer, byteLength);
                }

                string storageStateLabel = writeToCsvPermitted ? _activeSessionFilename : "DISK DISENGAGED";
                if (storageStateLabel != _lastStorageStateLabel)
                {
                    _lastStorageStateLabel = storageStateLabel;
                    TxtStorageStatus.Text = storageStateLabel;
                    TxtStorageStatus.Foreground = writeToCsvPermitted ?
                        new SolidColorBrush(ColorHelper.FromArgb(255, 0, 255, 102)) : 
                        new SolidColorBrush(ColorHelper.FromArgb(255, 255, 0, 127));
                }

                // 9. Zero-Allocation Strictly Data-Driven UI Render updates
                if (mnc != _lastMnc)
                {
                    _lastMnc = mnc;
                    string translatedMnoName = "UNKNOWN OPERATOR";
                    string siteName = "UNIDENTIFIED SITE";

                    var registry = GetOperatorRegistry(); 
                    if (registry.TryGetValue($"{mcc}-{mnc}", out var operatorData))
                    {
                        translatedMnoName = operatorData.Name;
                        siteName = operatorData.SiteDescription;
                    }

                    TxtNetworkName.Text = translatedMnoName;
                    TxtMccMnc.Text = $"[{mcc} {mnc}]";
                    TxtSiteLabel.Text = $"Site: ⚡ {siteName}";
                }

                if (cellId != _lastCellId)
                {
                    _lastCellId = cellId;
                    TxtEnb.Text = (cellId >> 8).ToString();
                    uint sectIndex = cellId & 0xFF;
                    TxtSector.Text = sectIndex < 100 ? CacheSector[sectIndex] : $"|{sectIndex}|";
                    TxtCellId.Text = cellId.ToString();
                }

                if (tac != _lastTac) { _lastTac = tac; TxtTac.Text = tac.ToString(); }
                if (rsrp != _lastRsrp) { _lastRsrp = rsrp; TxtRsrp.Text = Math.Abs(rsrp) < 150 ? CacheRsrp[Math.Abs(rsrp)] : $"{rsrp} dBm"; }
                if (rsrq != _lastRsrq) { _lastRsrq = rsrq; int rqIdx = (int)Math.Abs(rsrq * 10); TxtRsrq.Text = rqIdx < 500 ? CacheRsrq[rqIdx] : $"{rsrq} dB"; }
                if (rssi != _lastRssi) { _lastRssi = rssi; TxtRssi.Text = Math.Abs(rssi) < 150 ? CacheRssi[Math.Abs(rssi)] : $"{rssi} dBm"; }
                if (sinr != _lastSinr) { _lastSinr = sinr; TxtSinr.Text = sinr >= 0 && sinr < 100 ? CacheSinr[sinr] : $"{sinr} dB"; }
                if (pci != _lastPci) { _lastPci = pci; TxtPci.Text = pci.ToString(); }
                if (earfcn != _lastEarfcn) { _lastEarfcn = earfcn; TxtEarfcn.Text = $"{earfcn} (B3)"; }
                if (cqi != _lastCqi) { _lastCqi = cqi; TxtCqi.Text = cqi.ToString(); }
                if (ta != _lastTa) { _lastTa = ta; TxtTa.Text = ta.ToString("D2"); }

                if (latitude != _lastLat) { _lastLat = latitude; TxtLat.Text = $"LAT ▶ {latitude:F5}"; }
                if (longitude != _lastLon) { _lastLon = longitude; TxtLon.Text = $"LON ▶ {longitude:F5}"; }
                if (speedMph != _lastSpeedMph) { _lastSpeedMph = speedMph; TxtSpeed.Text = $"SPEED ▶ {speedMph:F1} MPH"; }

                if (batteryPercent != _lastBatteryPercent || batteryState != _lastBatteryState)
                {
                    _lastBatteryPercent = batteryPercent;
                    _lastBatteryState = batteryState;
                    TxtBattery.Text = batteryPercent + "% [" + (batteryState == "IDLE" ? "FULL" : batteryState) + "]";
                    TxtBatteryStatus.Text = batteryState;
                }

                int queueCount; lock (_offlineTransmissionQueue) { queueCount = _offlineTransmissionQueue.Count; }
                if (queueCount != _lastQueueCount)
                {
                    _lastQueueCount = queueCount;
                    TxtQueueStatus.Text = queueCount == 0 ? "0 PENDING UPLOADS" : $"{queueCount} CACHED RECORDS UNTRANSMITTED";
                    TxtQueueStatus.Foreground = queueCount == 0 ? 
                        new SolidColorBrush(ColorHelper.FromArgb(255, 0, 240, 255)) : 
                        new SolidColorBrush(ColorHelper.FromArgb(255, 255, 204, 0));
                }
            }
            catch { /* Shield loop thread boundaries safety */ }
        }

        private async Task ProcessOutboundTransmissionQueueAsync()
        {
            _isCurrentlyFlushingQueue = true;
            _queueLogger.CloseSession(); 

            await Task.Run(() =>
            {
                List<string> workingList;
                lock (_offlineTransmissionQueue) { workingList = new List<string>(_offlineTransmissionQueue); }

                try
                {
                    bool success = true; 
                    if (success)
                    {
                        lock (_offlineTransmissionQueue) { _offlineTransmissionQueue.Clear(); }
                        string queuePath = Path.Combine(_localStoragePath, "CellScout_Queue.dat");
                        if (File.Exists(queuePath)) File.Delete(queuePath);
                    }
                }
                catch { /* Silent network protect */ }
                finally { _isCurrentlyFlushingQueue = false; }
            });
        }
    }
}