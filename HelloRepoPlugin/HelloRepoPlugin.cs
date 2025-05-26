using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Photon.Pun;
using UnityEngine;
using BepInEx;

[BepInPlugin("ru.yourname.repo.agent.extended", "RepoAgentExtended", "1.0.6")]
public sealed class RepoAgentExtended : BaseUnityPlugin
{
    // ───────── настройки ─────────
    private const string HOST = "127.0.0.1";
    private const int PORT = 5555;
    private const float SEND_HZ = 10f;
    private const float RAY_LEN = 3f;

    private static readonly int[] ANG = { 0, 45, 90, 135, 180, 225, 270, 315 };
    private const int MAX_ITEMS = 128;  // максимум координат предметов

    // ───────── сеть ─────────
    private UdpClient _udp;
    private IPEndPoint _ep;
    private float _tSend, _velTimer;

    // ───────── ссылки ─────────
    private PlayerController _pc;
    private CharacterController _cc;
    private Rigidbody _rb;

    private object _ph;
    private FieldInfo _fHp, _fHpMax;
    private FieldInfo _fRoomVolumeCheck;    // отражение для roomVolumeCheck

    // ───────── состояние ─────────
    private bool _stream;
    private int _hpPrev;
    private Vector3 _prevPos;

    // ───────── структуры ─────────
    [Serializable]
    private struct ItemPos
    {
        public float x, y, z;
        public ItemPos(Vector3 p) { x = p.x; y = p.y; z = p.z; }
    }

    [Serializable]
    private class Obs
    {
        public float pos_x, pos_z, yaw_sin, yaw_cos;
        public float vel_f, vel_s, vel_up;
        public bool grounded;

        public float hp_norm, stamina_norm;
        public float[] ray = new float[16];

        public float delta_hp;
        public int item_count;
        public ItemPos[] item_pos;
    }

    // ───────── инициализация ─────────
    private void Awake()
    {
        _udp = new UdpClient();
        _ep = new IPEndPoint(IPAddress.Parse(HOST), PORT);

        // пытаемся найти поле roomVolumeCheck (public/private/internal)
        _fRoomVolumeCheck = typeof(ValuableObject)
            .GetField("roomVolumeCheck", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Logger.LogInfo("[RepoAgentExtended] Init OK (v1.0.6)");
        StartCoroutine(PlayerLoop());
    }

    // ───────── главный цикл ─────────
    private IEnumerator PlayerLoop()
    {
        while (true)
        {
            while (!TrySetLocalPlayer())
                yield return new WaitForSeconds(0.2f);

            _stream = true;
            _tSend = 0f;
            _velTimer = 0f;
            _prevPos = _pc.transform.position;
            Logger.LogInfo("[Agent] → STREAM ON");

            while (_stream)
            {
                if (_pc == null || !_pc.isActiveAndEnabled) { _stream = false; break; }

                _tSend += Time.deltaTime;
                if (_tSend >= 1f / SEND_HZ)
                {
                    _tSend = 0f;
                    string json = JsonUtility.ToJson(BuildObs());
                    byte[] buf = Encoding.UTF8.GetBytes(json);
                    _udp.Send(buf, buf.Length, _ep);
                }
                yield return null;
            }
            Logger.LogInfo("[Agent] ← STREAM OFF");
        }
    }

    // ───────── поиск локального игрока ─────────
    private bool TrySetLocalPlayer()
    {
        foreach (var pc in FindObjectsOfType<PlayerController>())
        {
            if (pc.playerAvatarScript?.photonView?.IsMine != true) continue;

            _pc = pc;
            _cc = pc.GetComponentInChildren<CharacterController>(true);
            _rb = pc.GetComponentInChildren<Rigidbody>(true);

            _ph = pc.playerAvatarScript.playerHealth;
            if (_ph != null)
            {
                _fHp = _ph.GetType().GetField("health", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                       _ph.GetType().GetField("currentHealth", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _fHpMax = _ph.GetType().GetField("maxHealth", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                          _ph.GetType().GetField("maximumHealth", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_fHp == null || _fHpMax == null) _ph = null;
            }
            _hpPrev = _ph != null ? (int)_fHp.GetValue(_ph) : 0;
            return true;
        }
        return false;
    }

    // ───────── формируем наблюдение ─────────
    private Obs BuildObs()
    {
        var o = new Obs();
        var tr = _pc.transform;

        // позиция и ориентация
        o.pos_x = tr.position.x;
        o.pos_z = tr.position.z;
        float yaw = tr.eulerAngles.y * Mathf.Deg2Rad;
        o.yaw_sin = Mathf.Sin(yaw);
        o.yaw_cos = Mathf.Cos(yaw);

        // скорость
        Vector3 vel = _cc ? _cc.velocity : (_rb ? _rb.velocity : Vector3.zero);
        if (vel == Vector3.zero)
        {
            _velTimer += 1f / SEND_HZ;
            vel = (tr.position - _prevPos) / _velTimer;
            _prevPos = tr.position;
            _velTimer = 0f;
        }
        o.vel_f = Vector3.Dot(vel, tr.forward);
        o.vel_s = Vector3.Dot(vel, tr.right);
        o.vel_up = vel.y;
        o.grounded = _cc ? _cc.isGrounded : (_rb ? Mathf.Abs(_rb.velocity.y) < 0.01f : true);

        // здоровье и стамина
        int hp = 0, hpMax = 100;
        if (_ph != null) { hp = (int)_fHp.GetValue(_ph); hpMax = (int)_fHpMax.GetValue(_ph); }
        o.hp_norm = hpMax > 0 ? (float)hp / hpMax : 0f;
        o.stamina_norm = Mathf.Clamp01(_pc.EnergyCurrent / _pc.EnergyStart);

        // 16 лучей-лидаров
        Vector3 knee = tr.position + Vector3.up * 0.3f;
        Vector3 chest = tr.position + Vector3.up * 1.2f;
        for (int i = 0; i < 8; i++)
        {
            Vector3 dir = Quaternion.Euler(0, ANG[i], 0) * tr.forward;
            o.ray[i] = Physics.Raycast(knee, dir, out var h1, RAY_LEN, ~0, QueryTriggerInteraction.Ignore) ? h1.distance : RAY_LEN;
            o.ray[i + 8] = Physics.Raycast(chest, dir, out var h2, RAY_LEN, ~0, QueryTriggerInteraction.Ignore) ? h2.distance : RAY_LEN;
        }

        // предметы — только из тех же комнат
        var list = new List<ItemPos>(MAX_ITEMS);
        if (ValuableDirector.instance?.valuableList != null)
        {
            var playerRooms = _pc.playerAvatarScript.RoomVolumeCheck.CurrentRooms;

            foreach (var v in ValuableDirector.instance.valuableList)
            {
                if (v == null) continue;
                if (list.Count >= MAX_ITEMS) break;

                // 1) пробуем достать компонент через отражение
                RoomVolumeCheck check = null;
                if (_fRoomVolumeCheck != null)
                    check = _fRoomVolumeCheck.GetValue(v) as RoomVolumeCheck;

                // 2) если не получилось — берём напрямую как компонент
                if (check == null)
                    check = v.GetComponent<RoomVolumeCheck>();

                // 3) если вообще нет RoomVolumeCheck — пропускаем
                if (check == null) continue;

                // хотя бы одна общая комната?
                bool sameRoom = false;
                foreach (var r in check.CurrentRooms)
                {
                    if (playerRooms.Contains(r)) { sameRoom = true; break; }
                }
                if (!sameRoom) continue;

                list.Add(new ItemPos(v.transform.position));
            }
        }
        o.item_pos = list.ToArray();
        o.item_count = o.item_pos.Length;

        // изменение здоровья
        o.delta_hp = hp - _hpPrev;
        _hpPrev = hp;

        return o;
    }

    private void OnDestroy() => _udp?.Close();
}
