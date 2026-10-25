using System;
using System.IO;
using System.Diagnostics;
using System.Web.Script.Serialization;
using HarmonyLib;
using SandBox;
using TaleWorlds.Core;
using TaleWorlds.CampaignSystem;
using TaleWorlds.SaveSystem.Load;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;
using TaleWorlds.MountAndBlade.View;

namespace MNBL.CombatProbe
{
 public static class ProbeLog
 {
  static readonly object Gate=new object();
  public static readonly string Store=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MNBL.CombatProbe");
  public static void Write(string message) { Event("message",new {message}); }
  public static void Capture(string name) { var path=Path.Combine(Store,"run-"+Process.GetCurrentProcess().Id+"-"+name+".bmp");TaleWorlds.Engine.Utilities.TakeScreenshot(path);Event("screenshot_requested",new {name,path}); }
  public static void Event(string name,object data) { lock(Gate) { Directory.CreateDirectory(Store); File.AppendAllText(Path.Combine(Store,"run-"+Process.GetCurrentProcess().Id+".jsonl"),new JavaScriptSerializer().Serialize(new {utc=DateTime.UtcNow.ToString("O"),thread=System.Threading.Thread.CurrentThread.ManagedThreadId,name,data})+Environment.NewLine); } }
 }
 public sealed class SubModule:MBSubModuleBase
 {
  string mode; bool ready,started; float elapsed; UiProbeScenario uiScenario;
  internal static bool RunAnimation;
  protected override void OnSubModuleLoad()
  {
   Directory.CreateDirectory(ProbeLog.Store); var request=Path.Combine(ProbeLog.Store,"probe.once");
   if(File.Exists(request)) { if(DateTime.UtcNow-File.GetLastWriteTimeUtc(request)<TimeSpan.FromMinutes(5)) mode=File.ReadAllText(request).Trim(); File.Move(request,request+".claimed-"+Guid.NewGuid().ToString("N")); }
   if(mode!="combat" && mode!="ui" && mode!="ui-auto" && mode!="animation") {mode=null;return;}
   RunAnimation=mode=="animation";
   var harmony=new Harmony("mnbl.combat.feasibility"); harmony.PatchAll(typeof(SubModule).Assembly); AbilitiesProbe.Install(harmony);
   ProbeLog.Event("loaded",new {mode,modules=TaleWorlds.Engine.Utilities.GetModulesNames(),mvid=typeof(Mission).Assembly.ManifestModule.ModuleVersionId});
  }
  protected override void OnBeforeInitialModuleScreenSetAsRoot() { ready=true; }
  protected override void OnApplicationTick(float dt)
  {
   if(mode!=null) { var quit=Path.Combine(ProbeLog.Store,"quit.once"); if(File.Exists(quit)) {File.Delete(quit);uiScenario?.Cancel();ProbeLog.Write("requested_clean_exit");TaleWorlds.Engine.Utilities.QuitGame();return;} }
   if(started && mode=="ui-auto" && Game.Current!=null) {if(uiScenario==null)uiScenario=new UiProbeScenario();uiScenario.Tick(dt);}
   if(!ready||started||mode==null)return; elapsed+=dt;if(elapsed<4)return;started=true;
   try { if(mode=="combat" || mode=="animation") MBGameManager.StartNewGame(new ProbeGameManager()); else {
    var data=MBSaveLoad.LoadSaveGameData("mnbl_baseline_65da8d934c644e4a99ed7a310cc3726e");
    if(data==null||!data.Successful)throw new InvalidOperationException("Diagnostic save failed to load");
    MBGameManager.StartNewGame(new ProbeCampaignManager(data));
   }} catch(Exception e) { ProbeLog.Write(e.ToString()); }
  }
 }
 internal sealed class ProbeGameManager:CustomGameManager
 {
  public override void OnLoadFinished() { base.OnLoadFinished(); MissionState.OpenNew("CombatProbe",new MissionInitializerRecord("benchmark_battle_11"){PlayingInCampaignMode=false},mission=>new MissionBehavior[]{new AgentHumanAILogic(),new CombatProbe(),new AnimationProbeDriver(),ViewCreator.CreateMissionSingleplayerEscapeMenu(false),ViewCreator.CreateMissionAgentStatusUIHandler(mission),ViewCreator.CreateMissionSpectatorControlView(mission)},true,true); }
 }
 internal sealed class AnimationProbeDriver:MissionLogic
 {
  AnimationProbe animation; TaleWorlds.Engine.Camera camera;
  public override void OnMissionTick(float dt) {
   var fixture=CombatProbe.Active;
   if(camera==null && fixture?.Attacker!=null) {var screen=TaleWorlds.ScreenSystem.ScreenManager.TopScreen as TaleWorlds.MountAndBlade.View.Screens.MissionScreen;if(screen!=null){camera=TaleWorlds.Engine.Camera.CreateCamera();var p=fixture.Attacker.Position;camera.LookAt(p+new TaleWorlds.Library.Vec3(3,-4,2.5f),p+new TaleWorlds.Library.Vec3(0,0.8f,1.1f),TaleWorlds.Library.Vec3.Up);camera.SetFovVertical(0.9f,16f/9f,0.1f,2000f);screen.CustomCamera=camera;var frame=camera.Frame;Mission.SetCameraFrame(ref frame,1);}}
   if(!SubModule.RunAnimation||!CombatProbe.Complete)return;if(animation==null){animation=new AnimationProbe(fixture.Attacker);animation.Start();}animation.Tick(dt);
  }
  public override void OnRemoveBehavior(){animation?.Restore();var screen=TaleWorlds.ScreenSystem.ScreenManager.TopScreen as TaleWorlds.MountAndBlade.View.Screens.MissionScreen;if(screen!=null&&screen.CustomCamera==camera)screen.CustomCamera=null;camera=null;base.OnRemoveBehavior();}
 }
 internal sealed class ProbeCampaignManager:SandBoxGameManager,ISaveManager
 {
  internal ProbeCampaignManager(LoadResult data):base(data){}
  void Guard() { Campaign.Current.SandBoxManager.SandBoxSaveManager=this;Campaign.Current.SetTimeControlModeLock(false);Campaign.Current.TimeControlMode=CampaignTimeControlMode.Stop;Campaign.Current.SetTimeControlModeLock(true); }
  public override void OnGameInitializationFinished(Game game){base.OnGameInitializationFinished(game);Guard();}
  public override void OnLoadFinished(){Guard();base.OnLoadFinished();Guard();ProbeLog.Write("campaign.loaded paused save_guard=true");}
  public int GetAutoSaveInterval(){return -1;}public bool IsAutoSaveDisabled(){return true;}
  public void OnSaveOver(bool success,string name){ProbeLog.Event("unexpected_save",new {success,name});}
 }
}
