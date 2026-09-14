global using ACE.Common;
global using ACE.Database;
global using ACE.Server.Entity.Actions;
global using ACE.DatLoader;

global using ACE.Entity;
global using ACE.Entity.Enum;

global using ACE.Server.Command;
global using ACE.Server.Entity;
global using ACE.Server.Managers;
global using ACE.Server.Mods;
global using ACE.Server.Network;
global using ACE.Server.Network.GameMessages.Messages;
global using ACE.Server.WorldObjects;

global using HarmonyLib;

global using System.Collections.Concurrent;
global using System.Linq;
global using System.Text;
global using System.Text.Encodings.Web;
global using System.Text.Json;

// Both ACE.Server.Entity and ACE.Server.Physics.Common define a Landblock. This mod
// means the entity one almost everywhere; the physics one is spelled out in full at
// the two places it is genuinely needed.
global using Landblock = ACE.Server.Entity.Landblock;
global using LScape = ACE.Server.Physics.Common.LScape;
global using PhysicsObj = ACE.Server.Physics.PhysicsObj;
global using ServerObjectManager = ACE.Server.Physics.Managers.ServerObjectManager;
