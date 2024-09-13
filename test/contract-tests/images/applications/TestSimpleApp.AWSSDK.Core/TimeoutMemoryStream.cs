using System;
using System.IO;

public class TimeoutMemoryStream : MemoryStream
{
    public TimeoutMemoryStream(byte[] buffer) : base(buffer)
    {
    }

    private int _readTimeout;
    private int _writeTimeout;

    public override int ReadTimeout
    {
        get => _readTimeout;
        set => _readTimeout = value;
    }

    public override int WriteTimeout
    {
        get => _writeTimeout;
        set => _writeTimeout = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Simulate read timeout
        if (_readTimeout > 0)
        {
            System.Threading.Thread.Sleep(_readTimeout);
        }
        return base.Read(buffer, offset, count);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        // Simulate write timeout
        if (_writeTimeout > 0)
        {
            System.Threading.Thread.Sleep(_writeTimeout);
        }
        base.Write(buffer, offset, count);
    }
}