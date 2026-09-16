using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace ProductivityMcp.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private static readonly byte[] ActivateCommand = [1];
    private static readonly byte[] Acknowledgement = [1];

    private readonly Lock _sync = new();
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Action? _activate;
    private Task? _listener;
    private bool _activationPending;
    private bool _disposed;

    private SingleInstanceCoordinator(string name)
    {
        _pipeName = $"{name}-activate";
        _mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    public static SingleInstanceCoordinator Create(string? name = null) =>
        new(name ?? DefaultName());

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary application instance can listen for activation requests.");
        }

        if (_listener is not null)
        {
            throw new InvalidOperationException("The activation listener has already been started.");
        }

        _listener = ListenAsync(_shutdown.Token);
    }

    public void SetActivationHandler(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        Action? pending = null;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activate is not null)
            {
                throw new InvalidOperationException("The activation handler has already been set.");
            }

            _activate = activate;
            if (_activationPending)
            {
                _activationPending = false;
                pending = activate;
            }
        }

        pending?.Invoke();
    }

    public async Task<bool> ActivateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimary)
        {
            throw new InvalidOperationException("The primary application instance cannot activate itself.");
        }

        var timeout = TimeSpan.FromSeconds(5);
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(200, cancellationToken).ConfigureAwait(false);

                var processId = new byte[sizeof(int)];
                await client.ReadExactlyAsync(processId, cancellationToken).ConfigureAwait(false);
                ForegroundActivation.AllowProcess(BinaryPrimitives.ReadInt32LittleEndian(processId));

                await client.WriteAsync(ActivateCommand, cancellationToken).ConfigureAwait(false);
                await client.FlushAsync(cancellationToken).ConfigureAwait(false);

                var acknowledgement = new byte[1];
                await client.ReadExactlyAsync(acknowledgement, cancellationToken).ConfigureAwait(false);
                return acknowledgement[0] == Acknowledgement[0];
            }
            catch (TimeoutException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                if (elapsed.Elapsed >= timeout)
                {
                    return false;
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shutdown.Cancel();
        if (_listener is not null)
        {
            try
            {
                _listener.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
        _mutex.Dispose();
    }

    private static string DefaultName()
    {
        var session = OperatingSystem.IsWindows()
            ? Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture)
            : Environment.GetEnvironmentVariable("XDG_SESSION_ID")
                ?? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")
                ?? Environment.GetEnvironmentVariable("DISPLAY")
                ?? "default";
        var identity = $"{Environment.UserName}\n{session}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"pmcp-{Convert.ToHexString(hash.AsSpan(0, 8))}";
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await HandleActivationAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task HandleActivationAsync(Stream stream, CancellationToken cancellationToken)
    {
        var processId = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(processId, Environment.ProcessId);
        await stream.WriteAsync(processId, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var command = new byte[1];
        await stream.ReadExactlyAsync(command, cancellationToken).ConfigureAwait(false);
        if (command[0] == ActivateCommand[0])
        {
            QueueActivation();
            await stream.WriteAsync(Acknowledgement, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void QueueActivation()
    {
        Action? activate;
        lock (_sync)
        {
            activate = _activate;
            if (activate is null)
            {
                _activationPending = true;
            }
        }

        activate?.Invoke();
    }
}
