using System;
using System.Globalization;
using Godot;

namespace World;

/// <summary>
/// Port of src/world/player.js: walk/run/jump over the procedural height field,
/// wading in water, head bob and a slight sprint FOV. Click to capture the mouse, Esc to release.
/// </summary>
public partial class Player : Node3D
{
    [Export] public float EyeHeight = 1.75f;
    [Export] public float CrouchEyeHeight = 1.05f;
    [Export] public float ProneEyeHeight = 0.35f;
    [Export] public float WalkSpeed = 3.2f;
    [Export] public float RunSpeed = 6.4f;
    [Export] public float JumpSpeed = 4.5f;
    [Export] public float Gravity = 9.81f;
    [Export] public float Sensitivity = 0.0022f;

    enum Stance { Standing, Crouching, Prone }

    const float MinPhotoHeight = 0.25f;
    const float MaxPhotoHeight = 3.5f;
    const float PhotoHeightSpeed = 1f;

    Camera3D _camera;
    ChunkManager _chunks;
    Vector3 _velocity;
    float _yaw;
    float _pitch = -0.06f;
    float _bobTimer;
    Stance _stance;
    float _eyeHeight;

    public bool IsGrounded { get; private set; }

    /// <summary>Set by photo mode: a fixed field of view (degrees), or null for normal play.</summary>
    public float? FovOverride;
    /// <summary>Set by photo mode so the view moves equally steadily at every zoom level.</summary>
    public float SensitivityScale = 1f;
    public Camera3D Camera => _camera;
    /// <summary>Set by photo mode: extra (yaw, pitch) in radians on top of where you look (hand shake).</summary>
    public Vector2 LookOffset;
    /// <summary>Set by photo mode while a tripod exposure runs: no walking or looking.</summary>
    public bool Frozen;
    public bool PhotoCameraActive { get; set; }
    static bool Captured => Input.MouseMode == Input.MouseModeEnum.Captured;

    public override void _Ready()
    {
        _camera = GetNode<Camera3D>("Camera3D");
        _eyeHeight = EyeHeight;
        _chunks = GetNodeOrNull<ChunkManager>("../ChunkManager");
        Position = new Vector3(0, (float)Terrain.Height(0, 0) + 2, 0);

        // Debug: `-- --spawn=x,z,yawDegrees,pitchDegrees` starts somewhere else.
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (!arg.StartsWith("--spawn=")) continue;
            var v = Array.ConvertAll(arg.Substring(8).Split(','), s => float.Parse(s, CultureInfo.InvariantCulture));
            Position = new Vector3(v[0], (float)Terrain.Height(v[0], v[1]) + 0.5f, v[1]);
            if (v.Length > 2) _yaw = Mathf.DegToRad(v[2]);
            if (v.Length > 3) _pitch = Mathf.DegToRad(v[3]);
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } && !Captured)
            Input.MouseMode = Input.MouseModeEnum.Captured;
        else if (e is InputEventMouseMotion motion && Captured && !Frozen)
        {
            float sens = Sensitivity * SensitivityScale;
            _yaw -= motion.Relative.X * sens;
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * sens, -1.42f, 1.42f);
        }
        else if (Captured && !Frozen && !PhotoCameraActive &&
                 e is InputEventKey { Pressed: true, Echo: false } key)
        {
            switch (key.PhysicalKeycode)
            {
                case Key.C: _stance = _stance == Stance.Crouching ? Stance.Standing : Stance.Crouching; break;
                case Key.Z: _stance = _stance == Stance.Prone ? Stance.Standing : Stance.Prone; break;
                default: return;
            }
            GetViewport().SetInputAsHandled();
        }
    }

    bool Held(Key key) => Captured && !Frozen && Input.IsPhysicalKeyPressed(key);

    public override void _Process(double delta)
    {
        float dt = Mathf.Min((float)delta, 0.1f);
        var forward = new Vector3(-Mathf.Sin(_yaw), 0, -Mathf.Cos(_yaw));
        var right = new Vector3(Mathf.Cos(_yaw), 0, -Mathf.Sin(_yaw));

        var move = Vector3.Zero;
        if (Held(Key.W)) move += forward;
        if (Held(Key.S)) move -= forward;
        if (Held(Key.D)) move += right;
        if (Held(Key.A)) move -= right;
        bool moving = move.LengthSquared() > 0;
        if (moving) move = move.Normalized();

        bool run = Held(Key.Shift);
        var pos = Position;
        bool inWater = pos.Y < Terrain.WaterLevel + 0.15;
        float speed = (run ? RunSpeed : WalkSpeed) * (inWater ? 0.68f : 1f);

        float blend = 1 - Mathf.Exp(-(IsGrounded ? 18f : 8f) * dt);
        _velocity.X += (move.X * speed - _velocity.X) * blend;
        _velocity.Z += (move.Z * speed - _velocity.Z) * blend;

        if (Held(Key.Space) && (IsGrounded || inWater))
        {
            _velocity.Y = inWater ? JumpSpeed * 0.72f : JumpSpeed;
            IsGrounded = false;
        }
        _velocity.Y -= Gravity * dt;
        pos += _velocity * dt;
        // Slide around tree trunks and boulders.
        if (_chunks != null) pos = _chunks.ResolveCollisions(pos);

        float floorY = Mathf.Max((float)Terrain.Height(pos.X, pos.Z), (float)Terrain.WaterLevel - 0.8f);
        if (pos.Y <= floorY)
        {
            pos.Y = floorY;
            _velocity.Y = 0;
            IsGrounded = true;
        }
        else IsGrounded = false;
        Position = pos;

        if (PhotoCameraActive)
        {
            int vertical = (Held(Key.E) ? 1 : 0) - (Held(Key.Q) ? 1 : 0);
            _eyeHeight = Mathf.Clamp(_eyeHeight + vertical * PhotoHeightSpeed * dt,
                MinPhotoHeight, MaxPhotoHeight);
        }
        else
        {
            float targetHeight = _stance switch
            {
                Stance.Crouching => CrouchEyeHeight,
                Stance.Prone => ProneEyeHeight,
                _ => EyeHeight,
            };
            _eyeHeight = Mathf.Lerp(_eyeHeight, targetHeight, 1 - Mathf.Exp(-12f * dt));
        }

        float bob = 0;
        if (!PhotoCameraActive && IsGrounded && moving && !inWater)
        {
            _bobTimer += dt * (run ? 13.5f : 9.5f);
            bob = Mathf.Sin(_bobTimer) * (run ? 0.075f : 0.045f);
        }
        else _bobTimer = 0;

        float targetFov = moving && run ? 73 : 70;
        if (FovOverride is float fixedFov) _camera.Fov = fixedFov;
        else _camera.Fov += (targetFov - _camera.Fov) * (1 - Mathf.Exp(-10 * dt));

        Rotation = new Vector3(0, _yaw + LookOffset.X, 0);
        _camera.Position = new Vector3(0, _eyeHeight + bob, 0);
        _camera.Rotation = new Vector3(_pitch + LookOffset.Y, 0, 0);
    }
}
