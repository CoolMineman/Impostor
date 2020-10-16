using System.Diagnostics;

namespace Impostor.Server.Net
{
    public static class DiscordBotHelper {
        private static Process botProcess = new Process();

        public static void init()
        {
            botProcess.StartInfo.FileName = "/usr/bin/python3.7";
            botProcess.StartInfo.Arguments = "/media/ben/windows/Linux/ImpostorBot/src/ImpostorBot.py";
            botProcess.StartInfo.RedirectStandardInput = true;
            botProcess.Start();
        }

        public static void createGame(string id)
        {
            botProcess.StandardInput.WriteLine("create\t" + id);
        }

        public static void deleteGame(string id)
        {
            botProcess.StandardInput.WriteLine("delete\t" + id);
        }
    }
}