using System;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using System.Drawing;

class Program
{
    private static volatile bool running = true;
    private static readonly object dataLock = new object();
    
    // Shared data between threads
    private static float? cpuTemp = null;
    private static float? gpuTemp = null;
    private static int? cpuFan = null;
    private static int? gpuFan = null;
    private static int brightnessValue = 128; // Default brightness (0-255)

    // GUI and communication objects
    private static MainForm mainForm;
    private static SerialCommunication serial;
    private static RTSSSharedMemory rtss;
    private static bool rtssConnected = false;
    private static bool serialConnected = false;

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        
        // Create the main form but don't show it initially
        mainForm = new MainForm();
        
        // Set up system tray
        NotifyIcon trayIcon = new NotifyIcon();
        trayIcon.Text = "PC Hardware Monitor";
        trayIcon.Icon = SystemIcons.Information;
        trayIcon.Visible = true;

        // Double-click to show main window
        trayIcon.DoubleClick += (sender, e) => {
            mainForm.Show();
            mainForm.WindowState = FormWindowState.Normal;
            mainForm.BringToFront();
        };

        ContextMenuStrip contextMenu = new ContextMenuStrip();
        ToolStripMenuItem showItem = new ToolStripMenuItem("Show");
        showItem.Click += (sender, e) => {
            mainForm.Show();
            mainForm.WindowState = FormWindowState.Normal;
            mainForm.BringToFront();
        };
        
        ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (sender, e) => {
            running = false;
            trayIcon.Visible = false;
            Application.Exit();
        };
        
        contextMenu.Items.Add(showItem);
        contextMenu.Items.Add(exitItem);
        trayIcon.ContextMenuStrip = contextMenu;

        // Initialize connections
        rtss = new RTSSSharedMemory();
        rtssConnected = rtss.Connect();

        // Set up event handlers for the main form
        mainForm.OnConnectRequested += (port, baud) => {
            if (serial != null)
            {
                serial.Disconnect();
            }
            serial = new SerialCommunication(port, baud);
            serialConnected = serial.Connect();
            mainForm.UpdateConnectionStatus(serialConnected);
            return serialConnected;
        };

        mainForm.OnBrightnessChanged += (value) => {
            lock (dataLock)
            {
                brightnessValue = value;
            }
        };

        // Start temperature/fan monitoring thread (every 2 seconds)
        Thread tempThread = new Thread(() => TemperatureMonitoringThread())
        {
            IsBackground = true,
            Name = "TemperatureMonitor"
        };
        tempThread.Start();

        // Start FPS monitoring + serial transmission thread (10 Hz = 100ms)
        Thread fpsSerialThread = new Thread(() => FpsAndSerialThread(trayIcon))
        {
            IsBackground = true,
            Name = "FpsAndSerial"
        };
        fpsSerialThread.Start();

        // Show the main form initially
        mainForm.Show();

        // Run the application
        Application.Run();
        
        // Cleanup
        running = false;
        rtss?.Disconnect();
        serial?.Disconnect();
    }

    private static void TemperatureMonitoringThread()
    {
        Console.WriteLine("Started temperature monitoring thread (2s interval)");
        while (running)
        {
            try
            {
                float? newCpuTemp = WmiSensors.GetCpuTemp();
                float? newGpuTemp = WmiSensors.GetGpuTemp();
                int? newCpuFan = WmiSensors.GetCpuFanSpeed();
                int? newGpuFan = WmiSensors.GetGpuFanSpeed();

                lock (dataLock)
                {
                    cpuTemp = newCpuTemp;
                    gpuTemp = newGpuTemp;
                    cpuFan = newCpuFan;
                    gpuFan = newGpuFan;
                }

                // Update GUI with new values
                if (mainForm != null && !mainForm.IsDisposed)
                {
                    mainForm.BeginInvoke(new Action(() => {
                        mainForm.UpdateSensorValues(newCpuTemp, newGpuTemp, newCpuFan, newGpuFan);
                    }));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in temperature thread: {ex.Message}");
            }

            Thread.Sleep(2000); // 2 seconds
        }
        Console.WriteLine("Temperature monitoring thread stopped");
    }

    private static void FpsAndSerialThread(NotifyIcon trayIcon)
    {
        Console.WriteLine("Started FPS + serial thread (10Hz = 100ms interval)");
        while (running)
        {
            try
            {
                // Get current FPS
                string activeProcess = rtss?.GetActiveWindowProcess();
                float? fps = rtssConnected && !string.IsNullOrEmpty(activeProcess)
                    ? rtss.ReadFpsForProcess(activeProcess)
                    : null;

                // Get current temperature/fan/brightness snapshot
                float? currentCpuTemp, currentGpuTemp;
                int? currentCpuFan, currentGpuFan;
                int currentBrightness;
                
                lock (dataLock)
                {
                    currentCpuTemp = cpuTemp;
                    currentGpuTemp = gpuTemp;
                    currentCpuFan = cpuFan;
                    currentGpuFan = gpuFan;
                    currentBrightness = brightnessValue;
                }

                // Send data via serial
                if (serialConnected && serial != null)
                {
                    bool sendResult = serial.SendData(currentCpuTemp, currentGpuTemp, fps, currentGpuFan, currentBrightness);
                    if (!sendResult)
                    {
                        Console.WriteLine("Serial connection failed. Disconnecting...");
                        serialConnected = false;
                        if (mainForm != null && !mainForm.IsDisposed)
                        {
                            mainForm.BeginInvoke(new Action(() => {
                                mainForm.UpdateConnectionStatus(false);
                            }));
                        }
                    }
                }

                // Update GUI with FPS
                if (mainForm != null && !mainForm.IsDisposed)
                {
                    mainForm.BeginInvoke(new Action(() => {
                        mainForm.UpdateFpsValue(fps);
                    }));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in FPS+serial thread: {ex.Message}");
            }

            Thread.Sleep(100); // 100ms = 10 Hz
        }
        Console.WriteLine("FPS + serial thread stopped");
    }
}

// --- Main Form ---
public partial class MainForm : Form
{
    private ComboBox comPortComboBox;
    private Button connectButton;
    private TrackBar brightnessSlider;
    private Label brightnessLabel;
    private Label connectionStatusLabel;
    private Label cpuTempLabel;
    private Label gpuTempLabel;
    private Label cpuFanLabel;
    private Label gpuFanLabel;
    private Label fpsLabel;

    public event Func<string, int, bool> OnConnectRequested;
    public event Action<int> OnBrightnessChanged;

    public MainForm()
    {
        InitializeComponent();
        LoadAvailablePorts();
    }

    private void InitializeComponent()
    {
        this.Text = "PC Hardware Monitor";
        this.Size = new Size(400, 350);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = true;
        this.ShowInTaskbar = false;

        // Override the form closing behavior
        this.FormClosing += (sender, e) => {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
        };

        // COM Port selection
        var portLabel = new Label();
        portLabel.Text = "COM Port:";
        portLabel.Location = new Point(15, 15);
        portLabel.Size = new Size(70, 20);
        this.Controls.Add(portLabel);

        comPortComboBox = new ComboBox();
        comPortComboBox.Location = new Point(90, 12);
        comPortComboBox.Size = new Size(100, 25);
        comPortComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        this.Controls.Add(comPortComboBox);

        // Refresh button
        var refreshButton = new Button();
        refreshButton.Text = "Refresh";
        refreshButton.Location = new Point(200, 12);
        refreshButton.Size = new Size(70, 25);
        refreshButton.Click += (sender, e) => LoadAvailablePorts();
        this.Controls.Add(refreshButton);

        // Connect button
        connectButton = new Button();
        connectButton.Text = "Connect";
        connectButton.Location = new Point(280, 12);
        connectButton.Size = new Size(80, 25);
        connectButton.Click += ConnectButton_Click;
        this.Controls.Add(connectButton);

        // Connection status
        connectionStatusLabel = new Label();
        connectionStatusLabel.Text = "Status: Disconnected";
        connectionStatusLabel.Location = new Point(15, 45);
        connectionStatusLabel.Size = new Size(200, 20);
        connectionStatusLabel.ForeColor = Color.Red;
        this.Controls.Add(connectionStatusLabel);

        // Brightness slider
        var brightnessLabelTitle = new Label();
        brightnessLabelTitle.Text = "Brightness:";
        brightnessLabelTitle.Location = new Point(15, 80);
        brightnessLabelTitle.Size = new Size(70, 20);
        this.Controls.Add(brightnessLabelTitle);

        brightnessSlider = new TrackBar();
        brightnessSlider.Location = new Point(90, 75);
        brightnessSlider.Size = new Size(200, 45);
        brightnessSlider.Minimum = 0;
        brightnessSlider.Maximum = 255;
        brightnessSlider.Value = 128;
        brightnessSlider.TickFrequency = 25;
        brightnessSlider.ValueChanged += BrightnessSlider_ValueChanged;
        this.Controls.Add(brightnessSlider);

        brightnessLabel = new Label();
        brightnessLabel.Text = "128";
        brightnessLabel.Location = new Point(300, 85);
        brightnessLabel.Size = new Size(50, 20);
        this.Controls.Add(brightnessLabel);

        // Sensor values
        cpuTempLabel = new Label();
        cpuTempLabel.Text = "CPU Temp: --";
        cpuTempLabel.Location = new Point(15, 135);
        cpuTempLabel.Size = new Size(150, 20);
        this.Controls.Add(cpuTempLabel);

        gpuTempLabel = new Label();
        gpuTempLabel.Text = "GPU Temp: --";
        gpuTempLabel.Location = new Point(15, 160);
        gpuTempLabel.Size = new Size(150, 20);
        this.Controls.Add(gpuTempLabel);

        cpuFanLabel = new Label();
        cpuFanLabel.Text = "CPU Fan: --";
        cpuFanLabel.Location = new Point(200, 135);
        cpuFanLabel.Size = new Size(150, 20);
        this.Controls.Add(cpuFanLabel);

        gpuFanLabel = new Label();
        gpuFanLabel.Text = "GPU Fan: --";
        gpuFanLabel.Location = new Point(200, 160);
        gpuFanLabel.Size = new Size(150, 20);
        this.Controls.Add(gpuFanLabel);

        fpsLabel = new Label();
        fpsLabel.Text = "FPS: --";
        fpsLabel.Location = new Point(15, 185);
        fpsLabel.Size = new Size(150, 20);
        this.Controls.Add(fpsLabel);
    }

    private void LoadAvailablePorts()
    {
        comPortComboBox.Items.Clear();
        string[] ports = SerialPort.GetPortNames();
        foreach (string port in ports)
        {
            comPortComboBox.Items.Add(port);
        }
        if (comPortComboBox.Items.Count > 0)
        {
            comPortComboBox.SelectedIndex = 0;
        }
    }

    private void ConnectButton_Click(object sender, EventArgs e)
    {
        if (comPortComboBox.SelectedItem == null)
        {
            MessageBox.Show("Please select a COM port first.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string selectedPort = comPortComboBox.SelectedItem.ToString();
        bool connected = OnConnectRequested?.Invoke(selectedPort, 115200) ?? false;
        
        UpdateConnectionStatus(connected);
    }

    private void BrightnessSlider_ValueChanged(object sender, EventArgs e)
    {
        int value = brightnessSlider.Value;
        brightnessLabel.Text = value.ToString();
        OnBrightnessChanged?.Invoke(value);
    }

    public void UpdateConnectionStatus(bool connected)
    {
        if (this.InvokeRequired)
        {
            this.Invoke(new Action<bool>(UpdateConnectionStatus), connected);
            return;
        }

        connectionStatusLabel.Text = connected ? "Status: Connected" : "Status: Disconnected";
        connectionStatusLabel.ForeColor = connected ? Color.Green : Color.Red;
        connectButton.Enabled = !connected;
        comPortComboBox.Enabled = !connected;
    }

    public void UpdateSensorValues(float? cpuTemp, float? gpuTemp, int? cpuFan, int? gpuFan)
    {
        if (this.InvokeRequired)
        {
            this.Invoke(new Action<float?, float?, int?, int?>(UpdateSensorValues), cpuTemp, gpuTemp, cpuFan, gpuFan);
            return;
        }

        cpuTempLabel.Text = $"CPU Temp: {(cpuTemp?.ToString("F1") ?? "--")}°C";
        gpuTempLabel.Text = $"GPU Temp: {(gpuTemp?.ToString("F1") ?? "--")}°C";
        cpuFanLabel.Text = $"CPU Fan: {(cpuFan?.ToString() ?? "--")} RPM";
        gpuFanLabel.Text = $"GPU Fan: {(gpuFan?.ToString() ?? "--")} RPM";
    }

    public void UpdateFpsValue(float? fps)
    {
        if (this.InvokeRequired)
        {
            this.Invoke(new Action<float?>(UpdateFpsValue), fps);
            return;
        }

        fpsLabel.Text = $"FPS: {(fps?.ToString("F1") ?? "--")}";
    }
}

// --- RTSS Shared Memory Reader ---
class RTSSSharedMemory
{
    // P/Invoke for Win32 API
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenFileMapping(uint dwDesiredAccess, bool bInheritHandle, string lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    IntPtr sharedMemory = IntPtr.Zero;
    IntPtr mappedView = IntPtr.Zero;

    public bool Connect()
    {
        string[] names = { "RTSSSharedMemoryV2", "RTSSSharedMemoryV1", "RTSSSharedMemory" };
        foreach (var name in names)
        {
            sharedMemory = OpenFileMapping(0x0004, false, name);
            if (sharedMemory != IntPtr.Zero)
            {
                mappedView = MapViewOfFile(sharedMemory, 0x0004, 0, 0, (UIntPtr)(4 * 1024 * 1024));
                if (mappedView != IntPtr.Zero)
                {
                    Console.WriteLine($"✓ Connected to {name}");
                    return true;
                }
                CloseHandle(sharedMemory);
            }
        }
        Console.WriteLine("✗ Could not connect to RivaTuner (make sure it's running)");
        return false;
    }

    public string GetActiveWindowProcess()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        try
        {
            var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName + ".exe";
        }
        catch { return null; }
    }

    public float? ReadFpsForProcess(string targetProcessName)
    {
        if (mappedView == IntPtr.Zero || string.IsNullOrEmpty(targetProcessName)) return null;
        try
        {
            byte[] header = new byte[256];
            Marshal.Copy(mappedView, header, 0, 256);

            uint dwSignature = BitConverter.ToUInt32(header, 0x00);
            uint dwAppEntrySize = BitConverter.ToUInt32(header, 0x08);
            uint dwAppArrOffset = BitConverter.ToUInt32(header, 0x0C);
            uint dwAppArrSize = BitConverter.ToUInt32(header, 0x10);

            for (int i = 0; i < Math.Min(dwAppArrSize, 50); i++)
            {
                int entryOffset = (int)dwAppArrOffset + i * (int)dwAppEntrySize;
                IntPtr entryPtr = IntPtr.Add(mappedView, entryOffset);
                byte[] entry = new byte[dwAppEntrySize];
                Marshal.Copy(entryPtr, entry, 0, entry.Length);

                // Process name is at offset 4, null-terminated
                int nameStart = 4;
                int nameEnd = Array.IndexOf(entry, (byte)0, nameStart);
                if (nameEnd > nameStart)
                {
                    string name = Encoding.UTF8.GetString(entry, nameStart, nameEnd - nameStart);
                    string exeName = System.IO.Path.GetFileName(name);
                    if (exeName.Equals(targetProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        int timingOffset = 0x10C;
                        if (timingOffset + 16 <= entry.Length)
                        {
                            uint dwTime0 = BitConverter.ToUInt32(entry, timingOffset);
                            uint dwTime1 = BitConverter.ToUInt32(entry, timingOffset + 4);
                            uint dwFrames = BitConverter.ToUInt32(entry, timingOffset + 8);
                            if (dwTime1 > dwTime0 && dwFrames > 0)
                            {
                                float fps = 1000.0f * dwFrames / (dwTime1 - dwTime0);
                                if (fps >= 1 && fps <= 1000) return (float)Math.Round(fps, 1);
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public void Disconnect()
    {
        if (mappedView != IntPtr.Zero) UnmapViewOfFile(mappedView);
        if (sharedMemory != IntPtr.Zero) CloseHandle(sharedMemory);
    }
}

// --- Serial Communication ---
class SerialCommunication
{
    private SerialPort serialPort;
    public SerialCommunication(string port, int baud)
    {
        serialPort = new SerialPort(port, baud);
        serialPort.NewLine = "\n";
    }
    public bool Connect()
    {
        try
        {
            serialPort.Open();
            Console.WriteLine($"✓ Connected to {serialPort.PortName} at {serialPort.BaudRate} baud");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Failed to connect to {serialPort.PortName}: {ex.Message}");
            return false;
        }
    }
    public bool SendData(float? cpuTemp, float? gpuTemp, float? fps, int? gpuFan, int brightness)
    {
        try
        {
            var data = new
            {
                cpu_temp = cpuTemp ?? -1,
                gpu_temp = gpuTemp ?? -1,
                fps = fps ?? -1,
                gpu_fan_speed = gpuFan ?? -1,
                brightness = brightness,
                timestamp = DateTimeOffset.Now.ToUnixTimeSeconds()
            };
            string json = JsonSerializer.Serialize(data) + "\n";
            serialPort.Write(json);
            serialPort.BaseStream.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }
    public void Disconnect()
    {
        if (serialPort.IsOpen)
            serialPort.Close();
    }
}

// --- WMI Sensors ---
static class WmiSensors
{
    public static float? GetCpuTemp()
    {
        try
        {
            var searcher = new ManagementObjectSearcher(@"root\LibreHardwareMonitor", "SELECT * FROM Sensor WHERE SensorType='Temperature'");
            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                if (name.Contains("Core Average"))
                {
                    var val = obj["Value"];
                    if (val != null && float.TryParse(val.ToString(), out float temp) && temp > 0)
                        return temp;
                }
            }
        }
        catch { }
        return null;
    }

    public static float? GetGpuTemp()
    {
        try
        {
            var searcher = new ManagementObjectSearcher(@"root\LibreHardwareMonitor", "SELECT * FROM Sensor WHERE SensorType='Temperature'");
            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                if (name.ToUpper().Contains("GPU CORE"))
                {
                    var val = obj["Value"];
                    if (val != null && float.TryParse(val.ToString(), out float temp) && temp > 0)
                        return temp;
                }
            }
        }
        catch { }
        return null;
    }
    
    public static void DebugPrintAllFanSensors()
    {
        try
        {
            var searcher = new ManagementObjectSearcher(@"root\LibreHardwareMonitor", "SELECT * FROM Sensor WHERE SensorType='Fan'");
            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                string id = obj["Identifier"]?.ToString() ?? "";
                var val = obj["Value"];
                Console.WriteLine($"Fan Sensor: '{name}' (ID: {id}), Value: {val}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error reading fan sensors: " + ex.Message);
        }
    }

    public static int? GetGpuFanSpeed()
    {
        try
        {
            var searcher = new ManagementObjectSearcher(@"root\LibreHardwareMonitor", "SELECT * FROM Sensor WHERE SensorType='Fan'");
            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                if (name.Equals("GPU Fan 1", StringComparison.OrdinalIgnoreCase))
                {
                    var val = obj["Value"];
                    if (val != null && int.TryParse(val.ToString(), out int rpm) && rpm > 0)
                        return rpm;
                }
            }
        }
        catch { }
        return null;
    }

    public static int? GetCpuFanSpeed()
    {
        try
        {
            var searcher = new ManagementObjectSearcher(@"root\LibreHardwareMonitor", "SELECT * FROM Sensor WHERE SensorType='Fan'");
            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                if (name.Equals("Fan #2", StringComparison.OrdinalIgnoreCase) || (name.Equals("CPU Fan", StringComparison.OrdinalIgnoreCase)))
                {
                    var val = obj["Value"];
                    if (val != null && float.TryParse(val.ToString(), out float rpm) && rpm > 0)
                        return (int)Math.Round(rpm);
                }
            }
        }
        catch { }
        return null;
    }
}
