using Microsoft.AspNetCore.SignalR;
using RemotePCControl.WebInterface.Services;
using System.Threading.Tasks;

namespace RemotePCControl.WebInterface.Hubs
{
    public class ControlHub : Hub
    {
        private readonly ConnectionService _connectionService;

        public ControlHub(ConnectionService connectionService)
        {
            _connectionService = connectionService;
        }

        public async Task SendCommandToServer(string commandType, string targetIp, string parameters)
        {
            string connectionId = Context.ConnectionId;

            string message = "";
            if (commandType == "LOGIN")
            {
                message = $"LOGIN|{targetIp}|{parameters}";
            }
            else
            {
                message = $"COMMAND|{targetIp}|{commandType}|{parameters}";
            }

            await _connectionService.ProcessCommand(connectionId, message);
        }

        public async Task RequestSessions()
        {
            string connectionId = Context.ConnectionId;
            await _connectionService.ProcessCommand(connectionId, "LIST_SESSIONS");
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            _connectionService.CloseConnection(Context.ConnectionId);
            return base.OnDisconnectedAsync(exception);
        }
    }
}