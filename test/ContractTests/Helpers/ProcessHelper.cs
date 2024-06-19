// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;

namespace AWS.Distro.Opentelemetry.AutoInstrumentation.Tests.IntegrationTests;

/// <summary>
/// Process std output / error helper
/// </summary>
public class ProcessHelper : IDisposable
{
    private readonly ManualResetEventSlim outputMutex = new ();
    private readonly StringBuilder outputBuffer = new ();
    private readonly StringBuilder errorBuffer = new ();
    private readonly object outputLock = new ();

    private bool isStdOutputDrained;
    private bool isErrOutputDrained;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProcessHelper"/> class.
    /// </summary>
    /// <param name="process">Process instance</param>
    public ProcessHelper(Process? process)
    {
        if (process == null)
        {
            return;
        }

        this.Process = process;
        this.Process.OutputDataReceived += (_, e) => this.DrainOutput(e.Data, this.outputBuffer, isErrorStream: false);
        this.Process.ErrorDataReceived += (_, e) => this.DrainOutput(e.Data, this.errorBuffer, isErrorStream: true);

        this.Process.BeginOutputReadLine();
        this.Process.BeginErrorReadLine();
    }

    /// <summary>
    /// Gets Process
    /// </summary>
    public Process? Process { get; }

    /// <summary>
    /// Gets standard output
    /// </summary>
    public string StandardOutput => this.CompleteOutput(this.outputBuffer);

    /// <summary>
    /// Gets standard error
    /// </summary>
    public string ErrorOutput => this.CompleteOutput(this.errorBuffer);

    /// <inheritdoc/>
    public void Dispose()
    {
        this.outputMutex.Dispose();
    }

    private bool Drain()
    {
        return this.Drain(TimeSpan.FromMinutes(5));
    }

    private bool Drain(TimeSpan timeout)
    {
        return this.outputMutex.Wait(timeout);
    }

    private void DrainOutput(string? data, StringBuilder buffer, bool isErrorStream)
    {
        if (data != null)
        {
            buffer.AppendLine(data);
            return;
        }

        lock (this.outputLock)
        {
            if (isErrorStream)
            {
                this.isErrOutputDrained = true;
            }
            else
            {
                this.isStdOutputDrained = true;
            }

            if (this.isStdOutputDrained && this.isErrOutputDrained)
            {
                this.outputMutex.Set();
            }
        }
    }

    private string CompleteOutput(StringBuilder builder)
    {
        if (this.Process == null || this.Process.HasExited)
        {
            return builder.ToString();
        }

        throw new InvalidOperationException("Process is still running and not ready to be drained.");
    }
}
