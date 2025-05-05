using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.Hooks;
using Microsoft.Xna.Framework;

namespace CaixasMisteriosas
{
    [ApiVersion(2, 1)]
    public class CaixasMisteriosas : TerrariaPlugin
    {
        public override string Name => "MysteryBox";
        public override string Author => " Brasilzinhoz";
        public override string Description => "Sistema de caixas com pré-visualização de itens";
        public override Version Version => new Version(2, 0, 0);

        private static string ConfigPath => Path.Combine(TShock.SavePath, "caixas_config.json");
        private Config Config;
        private Dictionary<int, DateTime> Cooldowns = new Dictionary<int, DateTime>();
        private Dictionary<int, int> CaixasEditando = new Dictionary<int, int>(); // playerIndex -> chestIndex
        private Dictionary<int, bool> ChestProtect = new Dictionary<int, bool>();

        public CaixasMisteriosas(Main game) : base(game)
        {
            Order = 1;
        }

        public override void Initialize()
        {
            ServerApi.Hooks.GamePostInitialize.Register(this, OnPostInitialize);
            ServerApi.Hooks.NetGetData.Register(this, OnGetData);
            Commands.ChatCommands.Add(new Command("caixas.admin", CaixaCmd, "caixa"));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GamePostInitialize.Deregister(this, OnPostInitialize);
                ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
            }
            base.Dispose(disposing);
        }

        private void OnPostInitialize(EventArgs args)
        {
            LoadConfig();
            foreach (var chest in Main.chest.Where(c => c != null))
            {
                ChestProtect[chest.x * 1000 + chest.y] = false;
            }
        }

        private void OnGetData(GetDataEventArgs args)
        {
            if (args.MsgID == PacketTypes.ChestOpen)
            {
                using (var reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length)))
                {
                    int x = reader.ReadInt16();
                    int y = reader.ReadInt16();
                    int chestIndex = reader.ReadInt16();

                    var player = TShock.Players[args.Msg.whoAmI];
                    if (player == null || !player.IsLoggedIn)
                        return;

                    var caixa = Config.Caixas.FirstOrDefault(c => c.X == x && c.Y == y);
                    if (caixa != null)
                    {
                        args.Handled = true;
                        AbrirCaixa(player, caixa);
                        return;
                    }
                    if (CaixasEditando.TryGetValue(player.Index, out int editChestIndex) && editChestIndex == chestIndex)
                    {
                        args.Handled = true;
                        player.SendInfoMessage("Você está editando esta caixa. Use /caixa addpremio <chance>");
                        return;
                    }
                }
            }
            else if (args.MsgID == PacketTypes.ChestGetContents)
            {
                var player = TShock.Players[args.Msg.whoAmI];
                if (player != null && ChestProtect.Values.Any(p => p))
                {
                    args.Handled = true;
                }
            }
        }

        private void OnPlayerDisconnect(LeaveEventArgs args)
        {
            CaixasEditando.Remove(args.Who);
            Cooldowns.Remove(args.Who);
        }


        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    Config = new Config();
                    File.WriteAllText(ConfigPath, Newtonsoft.Json.JsonConvert.SerializeObject(Config, Newtonsoft.Json.Formatting.Indented));
                    TShock.Log.ConsoleInfo("Configuração criada!");
                }
                else
                {
                    Config = Newtonsoft.Json.JsonConvert.DeserializeObject<Config>(File.ReadAllText(ConfigPath));
                    TShock.Log.ConsoleInfo($"Carregadas {Config.Caixas.Count} caixas configuradas");
                }
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("Erro ao carregar: " + ex.Message);
                Config = new Config();
            }
        }

        private void SaveConfig()
        {
            try
            {
                File.WriteAllText(ConfigPath, Newtonsoft.Json.JsonConvert.SerializeObject(Config, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("Erro ao salvar: " + ex.Message);
            }
        }

        private void AbrirCaixa(TSPlayer player, Config.CaixaConfig caixa)
        {
            if (Cooldowns.TryGetValue(player.Index, out DateTime lastUse))
            {
                double segundosRestantes = Config.CooldownSegundos - (DateTime.Now - lastUse).TotalSeconds;
                if (segundosRestantes > 0)
                {
                    player.SendErrorMessage($"Aguarde {segundosRestantes:0} segundos");
                    return;
                }
            }

            try
            {
                var random = new Random();
                double totalChance = caixa.Premios.Sum(p => p.Chance);
                double roll = random.NextDouble() * totalChance;
                double current = 0;
                Config.PremioConfig premio = null;

                foreach (var p in caixa.Premios.OrderByDescending(p => p.Chance))
                {
                    current += p.Chance;
                    if (roll <= current)
                    {
                        premio = p;
                        break;
                    }
                }

                if (premio == null)
                {
                    premio = caixa.Premios[0];
                }

                // Efeitos
                for (int i = 0; i < 15; i++)
                {
                    int dust = Dust.NewDust(player.TPlayer.position, player.TPlayer.width, player.TPlayer.height,
                                        89, 0f, 0f, 100, default, 2f);
                    Main.dust[dust].velocity *= 3f;
                }

                
                player.GiveItem(premio.ItemId, premio.Quantidade, premio.Prefixo);
                player.SendSuccessMessage($"Você ganhou: {Lang.GetItemNameValue(premio.ItemId)} x{premio.Quantidade}");

                Cooldowns[player.Index] = DateTime.Now;
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("Erro ao abrir caixa: " + ex.Message);
                player.SendErrorMessage("Erro ao abrir caixa!");
            }
        }

        private void CaixaCmd(CommandArgs args)
        {
            if (args.Parameters.Count < 1)
            {
                args.Player.SendErrorMessage("Use /caixa ajuda");
                return;
            }

            switch (args.Parameters[0].ToLower())
            {
                case "ajuda":
                    MostrarAjuda(args.Player);
                    break;
                case "setar":
                    SetarCaixa(args);
                    break;
                case "addpremio":
                    AdicionarPremio(args);
                    break;
                case "rempremio":
                    RemoverPremio(args);
                    break;
                case "listpremios":
                    ListarPremios(args);
                    break;
                case "cooldown":
                    ConfigurarCooldown(args);
                    break;
                case "reload":
                    RecarregarConfig(args.Player);
                    break;
                case "finalizar":
                    FinalizarEdicao(args.Player);
                    break;
                default:
                    args.Player.SendErrorMessage("Comando inválido. Use /caixa ajuda");
                    break;
            }
        }

        private void MostrarAjuda(TSPlayer player)
        {
            player.SendInfoMessage("=== Comandos Caixas ===");
            player.SendMessage("/caixa setar - Clica em um baú para configurá-lo", Color.White);
            player.SendMessage("/caixa addpremio <chance> - Adiciona item da sua mão como prêmio", Color.LightGreen);
            player.SendMessage("/caixa rempremio <slot> - Remove prêmio do slot", Color.Orange);
            player.SendMessage("/caixa listpremios - Mostra prêmios da caixa sendo editada", Color.LightBlue);
            player.SendMessage("/caixa finalizar - Termina a edição da caixa", Color.Yellow);
            player.SendMessage("/caixa cooldown <segundos> - Define tempo entre usos", Color.Pink);
        }

        private void SetarCaixa(CommandArgs args)
        {
            var player = args.Player;
            player.SendInfoMessage("Clique em um baú no mundo para configurá-lo como caixa misteriosa");

            player.AwaitingTempPoint = 2;
            player.TempPoints = new Point[2];

            player.AwaitingResponse["SetarCaixa"] = new Action<object>((o) =>
            {
                var points = (Point[])o;
                int x = points[0].X;
                int y = points[0].Y;

                int chestIndex = Chest.FindChest(x, y);
                if (chestIndex == -1)
                {
                    player.SendErrorMessage("Nenhum baú encontrado nesta posição!");
                    return;
                }

                var chest = Main.chest[chestIndex];
                if (chest == null)
                {
                    player.SendErrorMessage("Baú inválido!");
                    return;
                }

                if (Config.Caixas.Any(c => c.X == x && c.Y == y))
                {
                    player.SendErrorMessage("Este baú já está configurado!");
                    return;
                }

                var novaCaixa = new Config.CaixaConfig
                {
                    X = x,
                    Y = y,
                    Premios = new List<Config.PremioConfig>()
                };

                Config.Caixas.Add(novaCaixa);
                SaveConfig();

                CaixasEditando[player.Index] = chestIndex;
                ChestProtect[chest.x * 1000 + chest.y] = true;
                player.SendSuccessMessage($"Baú configurado! Agora adicione prêmios com /caixa addpremio <chance>");
            });
        }







        private void AdicionarPremio(CommandArgs args)
        {
            var player = args.Player;
            if (!CaixasEditando.TryGetValue(player.Index, out int chestIndex))
            {
                player.SendErrorMessage("Você não está editando nenhuma caixa! Use /caixa setar");
                return;
            }

            if (args.Parameters.Count < 2 || !double.TryParse(args.Parameters[1], out double chance))
            {
                player.SendErrorMessage("Use /caixa addpremio <chance> (ex: 0.5 para 50%)");
                return;
            }

            if (player.SelectedItem == null || player.SelectedItem.IsAir)
            {
                player.SendErrorMessage("Segure o item que deseja adicionar como prêmio");
                return;
            }

            var chest = Main.chest[chestIndex];
            var caixa = Config.Caixas.FirstOrDefault(c => c.X == chest.x && c.Y == chest.y);
            if (caixa == null)
            {
                player.SendErrorMessage("Caixa não encontrada na configuração!");
                return;
            }

            var premio = new Config.PremioConfig
            {
                ItemId = player.SelectedItem.type,
                Quantidade = player.SelectedItem.stack,
                Prefixo = player.SelectedItem.prefix,
                Chance = chance
            };

            caixa.Premios.Add(premio);
            SaveConfig();

            for (int i = 0; i < 40; i++) // Procura slot vazio
            {
                if (chest.item[i] == null || chest.item[i].IsAir)
                {
                    chest.item[i] = player.SelectedItem.Clone();
                    NetMessage.SendData((int)PacketTypes.ChestItem, -1, -1, null, chestIndex, i);
                    break;
                }
            }

            player.SendSuccessMessage($"Prêmio adicionado! Chance: {chance:0.##}");
        }

        private void RemoverPremio(CommandArgs args)
        {
            var player = args.Player;
            if (!CaixasEditando.TryGetValue(player.Index, out int chestIndex))
            {
                player.SendErrorMessage("Você não está editando nenhuma caixa!");
                return;
            }

            if (args.Parameters.Count < 2 || !int.TryParse(args.Parameters[1], out int slot))
            {
                player.SendErrorMessage("Use /caixa rempremio <slot> (veja os slots com /caixa listpremios)");
                return;
            }

            var chest = Main.chest[chestIndex];
            var caixa = Config.Caixas.FirstOrDefault(c => c.X == chest.x && c.Y == chest.y);
            if (caixa == null)
            {
                player.SendErrorMessage("Caixa não encontrada na configuração!");
                return;
            }

            if (slot < 0 || slot >= caixa.Premios.Count)
            {
                player.SendErrorMessage("Slot inválido!");
                return;
            }

            if (slot < chest.item.Length)
            {
                chest.item[slot] = new Item();
                NetMessage.SendData((int)PacketTypes.ChestItem, -1, -1, null, chestIndex, slot);
            }

            caixa.Premios.RemoveAt(slot);
            SaveConfig();

            player.SendSuccessMessage("Prêmio removido!");
        }

        private void ListarPremios(CommandArgs args)
        {
            var player = args.Player;
            if (!CaixasEditando.TryGetValue(player.Index, out int chestIndex))
            {
                player.SendErrorMessage("Você não está editando nenhuma caixa!");
                return;
            }

            var chest = Main.chest[chestIndex];
            var caixa = Config.Caixas.FirstOrDefault(c => c.X == chest.x && c.Y == chest.y);
            if (caixa == null)
            {
                player.SendErrorMessage("Caixa não encontrada na configuração!");
                return;
            }

            player.SendInfoMessage($"=== Prêmios (Chance Total: {caixa.Premios.Sum(p => p.Chance):0.##}) ===");
            for (int i = 0; i < caixa.Premios.Count; i++)
            {
                var premio = caixa.Premios[i];
                var item = TShock.Utils.GetItemById(premio.ItemId);
                player.SendMessage($"[Slot {i}] {item.Name} x{premio.Quantidade} (Chance: {premio.Chance:0.##})", Color.LightGreen);
            }
        }

        private void FinalizarEdicao(TSPlayer player)
        {
            if (CaixasEditando.TryGetValue(player.Index, out int chestIndex))
            {
                var chest = Main.chest[chestIndex];
                ChestProtect[chest.x * 1000 + chest.y] = false;
                CaixasEditando.Remove(player.Index);
                player.SendSuccessMessage("Edição finalizada! Caixa está pronta para uso.");
            }
            else
            {
                player.SendErrorMessage("Você não está editando nenhuma caixa!");
            }
        }

        private void ConfigurarCooldown(CommandArgs args)
        {
            if (args.Parameters.Count < 2 || !int.TryParse(args.Parameters[1], out int segundos))
            {
                args.Player.SendErrorMessage("Use /caixa cooldown <segundos>");
                return;
            }

            Config.CooldownSegundos = segundos;
            SaveConfig();
            args.Player.SendSuccessMessage($"Cooldown definido para {segundos} segundos");
        }

        private void RecarregarConfig(TSPlayer player)
        {
            LoadConfig();
            player.SendSuccessMessage("Configuração recarregada!");
        }
    }

    public class Config
    {
        public int CooldownSegundos { get; set; } = 30;
        public List<CaixaConfig> Caixas { get; set; } = new List<CaixaConfig>();

        public class CaixaConfig
        {
            public int X { get; set; }
            public int Y { get; set; }
            public List<PremioConfig> Premios { get; set; } = new List<PremioConfig>();
        }

        public class PremioConfig
        {
            public int ItemId { get; set; }
            public int Quantidade { get; set; }
            public byte Prefixo { get; set; }
            public double Chance { get; set; }
        }
    }
}
