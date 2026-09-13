// Convenience usings so patch files stay readable.
// Mirrors the set Aquafir's ACE.BaseMod template ships with, trimmed to what
// resolves against stock ACE (no ACE.Shared dependency).

global using ACE.Common;
global using ACE.Database;
global using ACE.Database.Models.Shard;
global using ACE.Database.Models.World;

global using ACE.Entity;
global using ACE.Entity.Enum;
global using ACE.Entity.Enum.Properties;

global using ACE.Server.Command;
global using ACE.Server.Command.Handlers;
global using ACE.Server.Entity;
global using ACE.Server.Entity.Actions;
global using ACE.Server.Factories;
global using ACE.Server.Managers;
global using ACE.Server.Mods;
global using ACE.Server.Network;
global using ACE.Server.Network.GameAction.Actions;
global using ACE.Server.Network.GameEvent.Events;
global using ACE.Server.Network.GameMessages.Messages;
global using ACE.Server.WorldObjects;

global using HarmonyLib;

global using System.Collections.Concurrent;
global using System.Globalization;
global using System.Reflection;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Serialization;

// ACE.Entity.Position vs ACE.Server.Entity types, and the two Biota/Weenie/Spell
// pairs, collide constantly. Pin the ones you almost always want.
global using Biota = ACE.Entity.Models.Biota;
global using Weenie = ACE.Entity.Models.Weenie;
global using Spell = ACE.Server.Entity.Spell;
