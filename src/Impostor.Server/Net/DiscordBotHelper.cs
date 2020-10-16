using System.Diagnostics;

namespace Impostor.Server.Net
{
    public static class DiscordBotHelper {
        private static Process botProcess = new Process();

        public static void init()
        {
            botProcess.StartInfo.FileName = "impostorbot";
            botProcess.Start();
        }

        public static void createGame(string id)
        {
            botProcess.StandardInput.WriteLine("create\t" + id);
        }
    }
}