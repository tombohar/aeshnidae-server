namespace Aeshnidae.Enlightenment;

/// <summary>
/// Every rule of enlightenment, in one file.
///
/// Retail enlightenment is a single hardcoded bargain: reset to level 1 and lose
/// society, luminance, auras, aetheria and your unspent xp, in exchange for +1 to
/// all skills, +2 vitality and a title. Aeshnidae keeps the shape and changes the
/// terms, so every clause of that bargain is a field here rather than a literal in
/// ACE's Enlightenment.cs.
///
/// The requirement gate is enforced here too, not in the world database. Retail
/// encodes it as an emote chain on the Font of Enlightenment (wcid 53412) - a
/// Goto/InqIntStat state machine with the numbers baked into two rows. That cannot
/// express "275 plus one per enlightenment you already have", and editing it means
/// a SQL migration every time a number moves. So the mod intercepts the chain at
/// its first Goto and answers the whole question in C#. The world database is left
/// exactly as it shipped.
/// </summary>
public class Settings
{
    public const string FileName = "Settings.json";

    /// <summary>
    /// Master switch. Off leaves ACE's retail enlightenment running untouched -
    /// the patches stay applied but hand every call straight back.
    /// </summary>
    public bool Enabled { get; set; } = true;

    // ---- who may enlighten --------------------------------------------------

    /// <summary>Level needed for a first enlightenment.</summary>
    public int BaseLevelRequirement { get; set; } = 275;

    /// <summary>
    /// Added to <see cref="BaseLevelRequirement"/> for each enlightenment already
    /// held, so the second costs 276, the third 277, and so on. Retail charged a
    /// flat 275 every time.
    ///
    /// Note this is the reason the gate cannot live in the world database: the
    /// InqIntStat row that guards it holds one min and one max, and this is a
    /// function of player state.
    /// </summary>
    public int LevelRequirementPerEnlightenment { get; set; } = 1;

    /// <summary>
    /// Hard ceiling on enlightenments. 0 is uncapped, which is the Aeshnidae
    /// default - retail stopped at 5 because that is how many titles exist, and
    /// <see cref="GrantTitles"/> handles that separately.
    /// </summary>
    public int MaxEnlightenments { get; set; } = 0;

    /// <summary>Free slots needed in the main pack, for the dequip and the certificate.</summary>
    public int RequiredFreeInventorySlots { get; set; } = 25;

    /// <summary>Require Master rank in one of the three societies.</summary>
    public bool RequireSocietyMaster { get; set; } = true;

    /// <summary>
    /// Require the full 65 luminance aura credits (everything except the two skill
    /// credit auras). Since <see cref="KeepLuminanceAuras"/> is on, this is bought
    /// once and satisfies every later enlightenment for free.
    /// </summary>
    public bool RequireAllLuminanceAuras { get; set; } = true;

    // ---- what survives ------------------------------------------------------

    /// <summary>
    /// Keep society membership and rank. Retail wiped both and stamped an
    /// "Enlightened&lt;Society&gt;Master" flag so the promotions officer could hand
    /// the rank straight back - a re-grind that existed only to be skipped.
    /// </summary>
    public bool KeepSociety { get; set; } = true;

    /// <summary>Keep the LumAug* aura ratings. Retail zeroed all thirteen.</summary>
    public bool KeepLuminanceAuras { get; set; } = true;

    /// <summary>
    /// Keep the quest flags that permit earning luminance at all. Retail erased
    /// them, so an enlightened character could not gain luminance until level 200
    /// and a re-run of Nalicana's Test.
    /// </summary>
    public bool KeepLuminanceAccess { get; set; } = true;

    /// <summary>
    /// Keep banked AvailableLuminance / MaximumLuminance. Not mentioned either way
    /// in the Aeshnidae design; kept because wiping the small unspent remainder
    /// while handing back all thirteen auras it buys would be noise, not cost.
    /// </summary>
    public bool KeepLuminanceBalance { get; set; } = true;

    /// <summary>
    /// Keep the unspent xp pool (AvailableExperience). Retail zeroed it.
    ///
    /// This is the clause that closes Aeshnidae.XpCurrency's open hole rather than
    /// patching it. While retail zeroed the pool, a player could /xp it to a friend,
    /// enlighten, and have it sent back - the transfer dodged a cost that this
    /// server no longer charges. With the pool surviving by design there is nothing
    /// to dodge, and the exploit stops being one.
    ///
    /// Turning this off reopens it. <see cref="Mod.WarnOnCouplings"/> logs a warning
    /// at startup if you do while XpCurrency is loaded.
    ///
    /// Only the *unspent* pool survives. Xp already sunk into levels, attributes and
    /// skill ranks is destroyed by the reset below, and that is still the bulk of a
    /// 275's lifetime earnings - so the cost of enlightening is unchanged in
    /// practice.
    /// </summary>
    public bool KeepUnassignedExperience { get; set; } = true;

    /// <summary>
    /// Keep aetheria slots and the mana field flags that open them. Off, per the
    /// design: losing the use of aetheria above your level requirement is one of
    /// the two things enlightenment is supposed to cost.
    /// </summary>
    public bool KeepAetheria { get; set; } = false;

    // ---- what resets --------------------------------------------------------

    /// <summary>Reset TotalExperience and Level to 1. This is what makes the rest bite.</summary>
    public bool ResetLevel { get; set; } = true;

    /// <summary>Reset skill ranks and recompute available skill credits.</summary>
    public bool ResetSkills { get; set; } = true;

    /// <summary>
    /// Reset attribute and vital ranks. Not named in the design's keep or lose
    /// list; on, because a level 1 character holding maxed attributes would make
    /// <see cref="ResetLevel"/> cosmetic.
    /// </summary>
    public bool ResetAttributes { get; set; } = true;

    /// <summary>
    /// Move everything equipped into the pack. The design's "lose the ability to
    /// use weapons and armour over your level requirement" is enforced by ACE's own
    /// wield requirements once the level drops; this is what stops you keeping the
    /// gear on in the meantime.
    /// </summary>
    public bool DequipAllItems { get; set; } = true;

    // ---- what you gain ------------------------------------------------------

    /// <summary>
    /// Award a title for the first five enlightenments (Awakened, Enlightened,
    /// Illuminated, Transcended, Cosmic Conscious). There is no sixth title in the
    /// client's dats, so enlightenments past five are silently untitled.
    /// </summary>
    public bool GrantTitles { get; set; } = true;

    /// <summary>Hand over an attribute reset certificate, wcid 46421.</summary>
    public bool GrantAttributeResetCertificate { get; set; } = true;

    /// <summary>Announce each enlightenment on the world broadcast channel.</summary>
    public bool BroadcastToServer { get; set; } = true;

    /// <summary>
    /// The three-beat effect at the Font: a private white-out and chant, the
    /// augmentation burst, the fireworks. Purely visual, and off means the
    /// enlightenment happens with only the chat lines.
    /// </summary>
    public bool Ceremony { get; set; } = true;

    // ---- the emote gate -----------------------------------------------------

    /// <summary>
    /// The Goto label on the Font of Enlightenment that starts its requirement
    /// chain. Intercepting it is what lets every setting above take effect without
    /// a SQL change - see EmoteGatePatch.
    /// </summary>
    public string GateLabel { get; set; } = "EnlightenmentCheck";

    /// <summary>
    /// The Goto label of the confirmation prompt to jump to once the mod's own
    /// checks pass. On wcid 53412 this is the InqYesNo that leads to the
    /// Enlightenment emote (type 9001).
    /// </summary>
    public string ConfirmLabel { get; set; } = "AbleToEnlighten";

    /// <summary>
    /// Seconds after an enlightenment during which the same character cannot start
    /// another. Belt and braces: the level reset already fails the gate on a second
    /// attempt, but this closes the window between two confirmations answered in
    /// the same tick, which is the shape most emote-chain double-fires take.
    /// </summary>
    public double ReentryGuardSeconds { get; set; } = 30.0;

    /// <summary>
    /// Require the player to still be standing at the Font when the grant fires,
    /// not merely when they asked.
    ///
    /// This is the clause that closes the exploit the NPC was meant to fix. The old
    /// /enlighten command reset the character and then sent them to their lifestone;
    /// run in portal space the teleport could not land, so the player kept their
    /// position and got a level 1 character inside high-tier content. Moving to an
    /// NPC removes the command but not the gap - the yes/no confirmation is
    /// asynchronous, so a player can open the prompt at the Font, recall away, and
    /// answer yes from somewhere else.
    ///
    /// Portal space and mid-teleport are refused regardless of this setting; it only
    /// governs the proximity test, which is the part with a tunable number in it.
    /// </summary>
    public bool RequireProximityToNpc { get; set; } = true;

    /// <summary>
    /// Metres the player may be from the Font when the grant fires. Generous - this
    /// is meant to catch recalls and portals, not to punish someone who stepped
    /// back while reading the prompt.
    /// </summary>
    public float ProximityRadius { get; set; } = 15.0f;

    // ---- notes, not settings ------------------------------------------------

    /// <summary>
    /// Two couplings this mod deliberately does not own, recorded where whoever
    /// edits the numbers above will see them.
    ///
    /// Aeshnidae.SkillMastery: mastery ranks survive enlightenment and retail ranks
    /// do not, so mastery priced below the retail rank it competes with makes
    /// everything on this page toothless - players would skip retail progression
    /// entirely and keep their whole build through the reset. That invariant is
    /// SkillMastery's to hold, in its own cost bands. Nothing here should try to
    /// preserve or restore mastery: it survives because it is stored outside the
    /// skill, not because anyone special-cases it, and this mod must never write
    /// into a CreatureSkill to "keep" anything - ResetSkill zeroes Ranks, InitLevel
    /// and ExperienceSpent, and six other ACE methods bare-assign InitLevel.
    ///
    /// The +1 to all skills and +2 vitality per enlightenment are not settings here
    /// because they are not this mod's to set: ACE applies them in CreatureSkill.Base
    /// and CreatureVital, derived from PropertyInt.Enlightenment. They already match
    /// the design. Making them tunable would mean a second postfix on
    /// CreatureSkill.Base, where SkillMastery already lives, and SkillMastery's
    /// client-display patches add their bonus to InitLevel independently rather than
    /// reading Base - so a delta added here would apply on the server and not show on
    /// the client. Left alone on purpose.
    /// </summary>
    [JsonIgnore]
    public string DesignNotes => "see Settings.cs";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings Load(string modPath)
    {
        var path = Path.Combine(modPath, FileName);

        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();

            var defaults = new Settings();
            defaults.Save(modPath);
            ModManager.Log($"[{Mod.Name}] wrote default settings to {path}");
            return defaults;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not read {path}, using defaults: {ex.Message}", ModManager.LogLevel.Warn);
            return new Settings();
        }
    }

    public void Save(string modPath)
    {
        try
        {
            File.WriteAllText(Path.Combine(modPath, FileName), JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not save settings: {ex.Message}", ModManager.LogLevel.Error);
        }
    }
}
