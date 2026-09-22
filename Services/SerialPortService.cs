using System.IO.Ports;
using System.Text;

namespace AAFSerialCaptureApp.Services;

/// <summary>Thin wrapper around SerialPort that raises an event for each complete line received.</summary>
public class SerialPortService : IDisposable
{
    private SerialPort? _port;
    private readonly StringBuilder _buffer = new();

    public bool IsOpen => _port?.IsOpen == true;

    public event EventHandler<string>? LineReceived;
    public event EventHandler<string>? ErrorOccurred;

    public static string[] GetPortNames() =>
        SerialPort.GetPortNames().OrderBy(p => p).ToArray();

    public void Connect(string portName, int baudRate)
    {
        Disconnect();

        _port = new SerialPort(portName, baudRate)
        {
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            NewLine = "\n",
            Encoding = Encoding.ASCII,
            // Many USB-serial/Arduino-style devices only transmit once DTR/RTS are asserted (PuTTY does this by default).
            DtrEnable = true,
            RtsEnable = true
        };
        _port.DataReceived += OnDataReceived;
        _port.ErrorReceived += (_, e) => ErrorOccurred?.Invoke(this, e.EventType.ToString());
        _port.Open();
    }

    public void Disconnect()
    {
        if (_port == null) return;
        try
        {
            _port.DataReceived -= OnDataReceived;
            if (_port.IsOpen) _port.Close();
        }
        catch
        {
            // ignore errors during shutdown
        }
        finally
        {
            _port.Dispose();
            _port = null;
            _buffer.Clear();
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_port == null) return;
        string chunk;
        try
        {
            chunk = _port.ReadExisting();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
            return;
        }

        lock (_buffer)
        {
            _buffer.Append(chunk);
            var text = _buffer.ToString();
            var lines = text.Split('\n');

            // Last element may be an incomplete line; keep it buffered.
            for (var i = 0; i < lines.Length - 1; i++)
            {
                var line = lines[i].TrimEnd('\r');
                LineReceived?.Invoke(this, line);
            }

            _buffer.Clear();
            _buffer.Append(lines[^1]);
        }
    }

    public void Dispose() => Disconnect();
}
