using Game.Net;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Local-only rain/snow particles in a box that follows the local player's camera.
    /// Emission rates come from the replicated weather state, so the presentation is
    /// identical on every peer without networking any particles.
    /// </summary>
    public class PrecipitationController : MonoBehaviour
    {
        private ParticleSystem _rain;
        private ParticleSystem _snow;

        private void Start()
        {
            _rain = CreateSystem("Rain", new Color(0.6f, 0.65f, 0.75f, 0.35f),
                speed: 22f, size: 0.03f, stretch: 8f);
            _snow = CreateSystem("Snow", new Color(0.9f, 0.9f, 0.95f, 0.8f),
                speed: 1.6f, size: 0.045f, stretch: 0f);
        }

        private void LateUpdate()
        {
            // Follow the player's position only - NOT the camera's forward. Offsetting by
            // look direction makes the whole rain volume swing when you turn, which reads
            // as the rain tilting and chasing you.
            var player = NetworkPlayer.Local;
            if (player != null)
            {
                Vector3 p = player.transform.position;
                transform.position = new Vector3(p.x, p.y + 14f, p.z);
            }

            var weather = WeatherManager.Instance;
            SetRate(_rain, weather != null ? weather.RainRate : 0f);
            SetRate(_snow, weather != null ? weather.SnowRate : 0f);
        }

        private static void SetRate(ParticleSystem ps, float rate)
        {
            if (ps == null) return;
            var emission = ps.emission;
            emission.rateOverTime = rate;
        }

        private ParticleSystem CreateSystem(string name, Color color, float speed, float size, float stretch)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.loop = true;
            main.startLifetime = 3f;
            main.startSpeed = speed;
            main.startSize = size;
            main.startColor = color;
            main.maxParticles = 4000;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = stretch > 0f ? 0.4f : 0.05f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            // Rotating the shape 90 deg about X points emission down, but it also remaps
            // the box axes: local Y -> world Z, local Z -> world -Y. So the "thin" axis
            // must be Z to get a horizontal ceiling rather than a vertical curtain.
            shape.rotation = new Vector3(90f, 0f, 0f);
            shape.scale = new Vector3(34f, 34f, 0.5f);

            var emission = ps.emission;
            emission.rateOverTime = 0f;

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = stretch > 0f
                ? ParticleSystemRenderMode.Stretch
                : ParticleSystemRenderMode.Billboard;
            // Stretched billboards align to velocity; keep length modest so drops read as
            // falling streaks rather than long slanted lines.
            renderer.velocityScale = 0f;
            renderer.lengthScale = stretch > 0f ? 3.5f : 1f;
            renderer.material = CreateParticleMaterial(color);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }

        private static Material CreateParticleMaterial(Color color)
        {
            // HDRP Unlit keeps precipitation visible regardless of lighting.
            var shader = Shader.Find("HDRP/Unlit");
            var mat = new Material(shader) { color = color };
            mat.SetFloat("_SurfaceType", 1f); // transparent
            mat.SetFloat("_BlendMode", 0f);
            mat.SetColor("_UnlitColor", color);
            mat.renderQueue = 3000;
            return mat;
        }
    }
}
