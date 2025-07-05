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

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        
        string[] ports = SerialPort.GetPortNames();
        Console.WriteLine("Available COM ports:");
        foreach (string port in ports)
        {
            Console.WriteLine(port);
        }
        
        NotifyIcon trayIcon = new NotifyIcon();
        trayIcon.Text = "PC Hardware Monitor";
        trayIcon.Icon = SystemIcons.Information;
        trayIcon.Visible = true;

        ContextMenuStrip contextMenu = new ContextMenuStrip();
        ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (sender, e) => {
            running = false;
            trayIcon.Visible = false;
            Application.Exit();
        };
        contextMenu.Items.Add(exitItem);
        trayIcon.ContextMenuStrip = contextMenu;

        // Initialize connections
        var rtss = new RTSSSharedMemory();
        bool rtssConnected = rtss.Connect();
        var serial = new SerialCommunication("COM6", 115200);
        bool serialConnected = serial.Connect();

        // Start temperature/fan monitoring thread (every 2 seconds)
        Thread tempThread = new Thread(() => TemperatureMonitoringThread())
        {
            IsBackground = true,
            Name = "TemperatureMonitor"
        };
        tempThread.Start();

        // Start FPS monitoring + serial transmission thread (10 Hz = 100ms)
        Thread fpsSerialThread = new Thread(() => FpsAndSerialThread(rtss, rtssConnected, serial, serialConnected, trayIcon))
        {
            IsBackground = true,
            Name = "FpsAndSerial"
        };
        fpsSerialThread.Start();

        // Run the tray application
        Application.Run();
        
        // Cleanup
        running = false;
        rtss.Disconnect();
        serial.Disconnect();
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

                // Temperature data updated silently
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in temperature thread: {ex.Message}");
            }

            Thread.Sleep(2000); // 2 seconds
        }
        Console.WriteLine("Temperature monitoring thread stopped");
    }

    private static void FpsAndSerialThread(RTSSSharedMemory rtss, bool rtssConnected, SerialCommunication serial, bool serialConnected, NotifyIcon trayIcon)
    {
        Console.WriteLine("Started FPS + serial thread (10Hz = 100ms interval)");
        while (running)
        {
            try
            {
                // Get current FPS
                string activeProcess = rtss.GetActiveWindowProcess();
                float? fps = rtssConnected && !string.IsNullOrEmpty(activeProcess)
                    ? rtss.ReadFpsForProcess(activeProcess)
                    : null;

                // Get current temperature/fan snapshot
                float? currentCpuTemp, currentGpuTemp;
                int? currentCpuFan, currentGpuFan;
                
                lock (dataLock)
                {
                    currentCpuTemp = cpuTemp;
                    currentGpuTemp = gpuTemp;
                    currentCpuFan = cpuFan;
                    currentGpuFan = gpuFan;
                }

                // Send data via serial
                if (serialConnected)
                {
                    bool sendResult = serial.SendData(currentCpuTemp, currentGpuTemp, fps, currentGpuFan);
                    if (!sendResult)
                    {
                        Console.WriteLine("Serial connection failed. Shutting down...");
                        running = false;
                        trayIcon.Visible = false;
                        Application.Exit();
                        break;
                    }
                }

                // FPS data updated silently
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
    public bool SendData(float? cpuTemp, float? gpuTemp, float? fps, int? gpuFan)
    {
        try
        {
            var data = new
            {
                cpu_temp = cpuTemp ?? -1,
                gpu_temp = gpuTemp ?? -1,
                fps = fps ?? -1,
                gpu_fan_speed = gpuFan ?? -1,
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
                if (name.Equals("Fan #2", StringComparison.OrdinalIgnoreCase))
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
