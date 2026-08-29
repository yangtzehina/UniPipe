namespace UniCli.Server.Editor
{
    /// <summary>
    /// The original transport: a named pipe whose name is derived from the project path, so the
    /// UniCli CLI finds the right editor with no configuration. Always present.
    /// </summary>
    public sealed class PipeTransportFactory : ICommandTransportFactory
    {
        private readonly string _pipeName;

        public PipeTransportFactory(string pipeName) => _pipeName = pipeName;

        public string Name => "pipe";

        public ICommandTransport Create(CommandReceivedHandler onCommandReceived)
            => new PipeServer(_pipeName, onCommandReceived);
    }
}
