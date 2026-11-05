// SPDX-License-Identifier: MIT
using Kiln;

namespace GamedataObserver;

/// <summary>Logs watched kiln.gamedata values whenever they change (every 25 steps).</summary>
public sealed class ObserverMod : Mod
{
    private static readonly (GameDataSection Section, string Entry, string Path)[] Watched =
    [
        (GameDataSection.Items, "Ammo_T1_Spell_Fire_Ball_lvl10", "impactValues.ManaCost"),
        (GameDataSection.Items, "Ammo_T1_Spell_Fire_Ball_lvl10", "uiValues[0].value"),
        (GameDataSection.Items, "Ability_DoubleJump", "impactValues.StaminaCost"),
        (GameDataSection.Templates, "Explosion_Player_Spell_Fireball_1", "ColliderResourceComponent.colliders[0].dataArray[0].shape.radius"),
        (GameDataSection.Templates, "Explosion_Player_Spell_Fireball_1", "ImpactConfiguration.configValues.NoiseRange"),
        (GameDataSection.Templates, "Explosion_Player_Spell_Fireball_1", "ImpactConfiguration.configValues.PushBackStrength"),
        (GameDataSection.Templates, "Enemy_Vukah_Melee", "Enemy01BehaviorState.aggroSetup.viewRange"),
        (GameDataSection.Templates, "Projectile_Player_Staff_Fireball_1", "LifeTime.lifeTime"),
        (GameDataSection.Sequences, "Attack_Player_Staff_FixedAnim_Fireball_fast", "events[SpawnEntityEvent#0].time"),
        (GameDataSection.Sequences, "Attack_Player_Staff_FixedAnim_Fireball_fast", "events[14].$type"),
        (GameDataSection.Arsenals, "template:Enemy_Vukah_Melee", "attacks[0].description.maxRange"),
        (GameDataSection.Skills, "item:Ability_DoubleJump", "costs"),
    ];
    private readonly Dictionary<string, string> _last = new();

    public override void OnLoad() => _ = Loop();

    private async Task Loop()
    {
        while (true)
        {
            for (var i = 0; i < 25; i++) await Context.Scheduler.NextTick();
            if (!Content.GameDataAvailable || !Content.Ready) continue;
            foreach (var (s, entry, path) in Watched)
            {
                string now;
                try
                {
                    var e = Content.GameData(s).Get(entry);
                    now = e == null ? "(none)" : path.EndsWith(".$type")
                        ? e.GetJson(path[..^6])?["value"]?["$type"]?.ToString() ?? "?"
                        : e.GetJson(path)?["absent"] is not null ? "(absent)" : e.GetJson(path)?["value"]?.ToJsonString() ?? "?";
                }
                catch (Exception ex)
                {
                    now = "(" + ex.GetType().Name + ")";
                }
                var key = $"{s} {entry}.{path}";
                if (_last.TryGetValue(key, out var old) && old != now) Log.Info($"OBSERVE {key}: {old} -> {now}");
                _last[key] = now;
            }
        }
    }
}
