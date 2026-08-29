using UnityEngine;
using FairyGUI;
using Unity.Pipeline.HotReload;

// Hot-reloadable UI tuning harness. Editor-only assembly, so FairyGUI itself carries no
// dependency on the automation package and nothing here can reach a player build.
//
// Iterate: edit a [HotReload] body below, then
//   unity command reload_file --filename Assets/UiHotTuning/UiHotTuning.cs
// The change applies in Play Mode without a domain reload.
//
// In-place reload only weaves instance methods returning void, and the body may touch
// public members only.
public class UiHotTuning : MonoBehaviour
{
    public GComponent panel;
    public GGraph background;
    public GTextField label;
    public int tick;

    void Start()
    {
        panel = new GComponent();
        panel.SetSize(360, 150);
        GRoot.inst.AddChild(panel);

        background = new GGraph();
        background.SetSize(360, 150);
        panel.AddChild(background);

        label = new GTextField();
        label.SetSize(340, 40);
        label.SetXY(14, 54);
        label.textFormat = new TextFormat() { size = 26, color = Color.white };
        panel.AddChild(label);
    }

    [HotReload]
    void Update()
    {
        tick = tick + 1;
        panel.SetXY(260, 30);
        background.DrawRect(panel.width, panel.height, 2, Color.white, new Color(0.2f, 0.35f, 0.75f, 1f));
        label.text = "tuning v3 decoupled  tick=" + tick;
    }
}
