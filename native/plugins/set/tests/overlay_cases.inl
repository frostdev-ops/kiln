// SPDX-License-Identifier: MIT
// Production SET tools, called with a fake kiln.ui function table; included after the Settings.define fixture.
{
 initTools();
 const auto work=root.parent_path()/"tools";std::filesystem::remove_all(work);std::filesystem::create_directories(work);
 setui::configure(work,work,sink);
 assert(setui::service()->open({nullptr,0})==KILN_ERR_UNAVAILABLE);
 setui::bind(&toolsUi,1);
 assert(toolsMenu&&toolsKey);
 assert(setui::service()->open({"one",3})==KILN_OK&&toolsDemand==1&&toolsCapture==1);
 {const std::string longest(257,'m');                                       // a mod name has at most 256 bytes
  assert(setui::service()->open({longest.data(),257})==KILN_ERR_UNAVAILABLE&&setui::service()->open({longest.data(),256})==KILN_OK);
  assert(setui::service()->open({"one",3})==KILN_OK);}
 toolsInputs={{"God mode","false"},{"regen","7.125"},{"count","4"},{"mode","1"},{"name","xy"}};
 const int top=lua_gettop(L);
 setui::draw();setui::draw();assert(toolsInputs.empty()&&toolsWindows==0&&toolsIds==0&&lua_gettop(L)==top);assert(logged("[SET UI] Opened tools"));
 must(L,one,"assert(s.god==false and s.regen==7.125 and s.count==4 and s.mode=='slow' and s.name=='xy' and changed==nil)");
 // Multiple frames coalesce edits; Lua is never entered on the drawing thread.
 toolsInputs={{"regen","8.25"}};setui::draw();
 settingsEdits(L);
 must(L,one,"assert(changed=='god=false;regen=8.25;count=4;mode=slow;name=xy;');changed=nil");
 // Bad numeric input stays editable but cannot enter the setting/file or enqueue onChange.
 toolsInputs={{"regen","nan"}};setui::draw();settingsEdits(L);must(L,one,"assert(s.regen==8.25 and changed==nil)");
 toolsInputs={{"regen","12.125"}};setui::draw();settingsEdits(L);must(L,one,"assert(s.regen==12.125 and changed=='regen=12.125;');changed=nil");
 // A stale page copy from before a reload cannot edit a replacement page with the same mod/key.
 auto old=mset::pages().front();mset::define(old);
 assert(!mset::set(old.mod,"regen",20,"",true,old.revision));
 // The double and large-int widgets must not round values through the float32 UI ABI.
 mset::Page precise;precise.mod="precision";const auto precisePath=root/"precision";std::filesystem::create_directory(precisePath);precise.path=precisePath.string();
 precise.fields={{"double","Precise double",mset::Number},{"integer","Large integer",mset::Int}};mset::define(precise);
 toolsClick="All mods";toolsInputs={{"Precise double","1.2345678901234567"},{"Large integer","9007199254740991"}};setui::draw();
 mset::Field exact;assert(mset::get("precision","double",exact)&&exact.num==1.2345678901234567);assert(mset::get("precision","integer",exact)&&exact.num==9007199254740991.0);
 mset::drop("precision",sink);
 // The id Kiln's mod list passes may differ in case from the mod's folder (Windows folder names don't): the same page.
 auto noPage=[]{return std::any_of(toolsRows.begin(),toolsRows.end(),[](const auto& r){return r.find("has not defined a settings page")!=std::string::npos;});};
 assert(setui::service()->open({"ONE",3})==KILN_OK);toolsRows.clear();setui::draw();
 if(noPage())std::cerr<<"settings: 'ONE' did not find mod one's page\n";
 assert(!noPage());
 assert(setui::service()->open({"nosuchmod",9})==KILN_OK);toolsRows.clear();setui::draw();assert(noPage());
 assert(setui::service()->open({nullptr,0})==KILN_OK);
 // Compatibility reads exactly the runtime's reasons, rather than inferring available from compatible.
 toolsTab="Compatibility";toolsRows.clear();setui::draw();
 assert(std::any_of(toolsRows.begin(),toolsRows.end(),[](const auto& s){return s.find("Dedicated server: No overlay")!=std::string::npos;}));
 // Support report is explicit, local and redactable; it does not execute or upload log content.
 std::ofstream(work/"kiln.log")<<"current test log "<<(getenv("HOME")?getenv("HOME"):"");
 std::ofstream(work/"kiln.prev.log")<<"previous test log";
 toolsTab="Bug Report";toolsClick="Save local support report";setui::draw();
 const auto report=slurp(work/"set-report.txt");
 assert(report.find("current test log")!=std::string::npos&&report.find("previous test log")!=std::string::npos&&report.find("game_build")!=std::string::npos);
 toolsInputs={{"Debug logs","false"}};setui::draw();assert(slurp(work/"set-ui.ini")=="debugLog=0\n");
 assert(setui::logText(false).find("current test log")!=std::string::npos&&setui::logText(false).find("previous test log")==std::string::npos);
 if(const char* home=getenv("HOME");home&&strlen(home)>3)assert(report.find(home)==std::string::npos&&report.find("%USERPROFILE%")!=std::string::npos);
 // Home folders are redacted by their shape too, whatever HOME and USERPROFILE say: Wine shows the Linux home as
 // Z:\home\<name>, Proton's profile is C:\users\steamuser, JSON doubles the separators. Either separator, any case.
 std::ofstream(work/"kiln.log")<<"mods in Z:\\home\\alicefix\\.local\\share\\Steam\\steamapps\n"
   "json \"C:\\\\Users\\\\Bob Fix\\\\AppData\\\\Local\"\n"
   "linux /home/carolfix/.steam and /var/home/davefix/x\n"
   "mac /Users/erinfix/Library and C:/USERS/frankfix/x and \\HOME\\gracefix\\y\n"
   "proton C:\\users\\steamuser\\AppData and Z:\\home\\heidifix\n";
 toolsTab="Bug Report";toolsClick="Save local support report";setui::draw();
 for(const auto& shaped:{setui::logText(false),slurp(work/"set-report.txt")}){
  bool leaked=false;
  for(const char* name:{"alicefix","Bob Fix","carolfix","davefix","erinfix","frankfix","gracefix","steamuser","heidifix"})
   if(shaped.find(name)!=std::string::npos){std::cerr<<"redaction: '"<<name<<"' left in\n";leaked=true;}
  assert(!leaked);
  assert(shaped.find("Z:\\home\\")!=std::string::npos&&shaped.find("\\.local\\share\\Steam\\steamapps")!=std::string::npos&&
         shaped.find("\\\\AppData\\\\Local")!=std::string::npos&&shaped.find("/Library and")!=std::string::npos);
 }
 // A long session's log: the copy and the report keep its end (where a crash is), at most 1 MiB of it, from a line
 // start. A mod's stray byte or non-ASCII text never makes the clipboard text invalid UTF-8.
 {std::ofstream big(work/"kiln.log",std::ios::binary);big<<"FIRST LINE of the session\n";
  for(int i=0;big.tellp()<(2<<20);++i)big<<"[set] line "<<i<<" from a mod: Grüße €\n";
  big<<"stray byte \xff from a mod\nLAST LINE before the crash\n";}
 toolsClick="Save local support report";setui::draw();
 {const auto copied=setui::logText(false),saved=slurp(work/"set-report.txt");
  if(copied.find("LAST LINE")==std::string::npos)std::cerr<<"log copy: the end of a 2 MiB kiln.log is missing ("<<copied.size()<<" bytes, starts '"<<copied.substr(0,26)<<"')\n";
  if(!validUtf8(copied))std::cerr<<"log copy: not valid UTF-8\n";
  assert(copied.find("LAST LINE before the crash")!=std::string::npos&&copied.find("FIRST LINE")==std::string::npos);
  assert(copied.rfind("<earlier lines omitted>\n[set] line ",0)==0&&copied.size()<=(1u<<20)+64&&validUtf8(copied));
  assert(saved.find("LAST LINE before the crash")!=std::string::npos&&saved.find("FIRST LINE")==std::string::npos);}
 // Refuse unsafe report destinations instead of following a player-provided link.
 std::filesystem::remove(work/"set-report.txt");std::filesystem::create_symlink(root/"secret.lua",work/"set-report.txt");
 toolsClick="Save local support report";setui::draw();assert(slurp(root/"secret.lua")=="return 'outside'");std::filesystem::remove(work/"set-report.txt");
 // Console queues, it does not run Lua on the renderer. Typing runs nothing, however many frames the field is drawn;
 // Enter queues the line once (and so does Run).
 toolsTab="Console";toolsRows.clear();toolsInputs={{"Lua","return 123"}};
 for(int frame=0;frame<3;++frame)setui::draw();
 {const auto typed=takeQueue();if(!typed.empty())std::cerr<<"console: "<<typed.size()<<" line(s) queued while typing, first '"<<typed.front()<<"'\n";assert(typed.empty());}
 toolsEnter="Lua";setui::draw();assert(takeQueue()==std::vector<std::string>{"return 123"});
 setui::draw();setui::draw();assert(takeQueue().empty()&&toolsInputs.empty());
 toolsInputs={{"Lua","return 7"}};toolsClick="Run";setui::draw();assert(takeQueue()==std::vector<std::string>{"return 7"});toolsInputs.clear();
 // The console history keeps the last 2,000 lines, each at most 4,096 bytes.
 toolsClick="Clear";setui::draw();
 for(int i=0;i<2001;++i)setui::line(("history "+std::to_string(i)).c_str());
 setui::line(std::string(5000,'L').c_str());
 toolsRows.clear();setui::draw();
 assert(toolsRows.size()==2000&&toolsRows.front()=="history 2"&&toolsRows.back()==std::string(4096,'L'));
 toolsClick="Clear";setui::draw();toolsRows.clear();setui::draw();assert(toolsRows.empty());
 assert(std::none_of(toolsRows.begin(),toolsRows.end(),[](const auto& row){return row.find("[SET UI]")!=std::string::npos;}));
 // Escape cancels a remap without closing tools, and modifier-only keys never become bindings.
 must(L,one,"Keys.hotkey('tools','Tools remap test',function()end)");rt::setBinding("one.tools",0,0x77);
 toolsTab="Bindings";toolsClick="F8";setui::draw();
 KilnUiKey cancel{};cancel.down=1;cancel.vk=0x1b;
 assert(toolsKey(&cancel,nullptr)&&setui::isOpen());setui::draw();
 toolsClick="F8";setui::draw();toolsPressed=0xa0;setui::draw();
 assert(toolsKey(&cancel,nullptr)&&setui::isOpen());setui::draw();
 // A window-thread close during rendering must not re-enable tick-side inspector work afterwards.
 toolsTab="Entities";toolsEscapeDuringBegin=true;setui::draw();
 assert(!setui::isOpen()&&!toolsDemand&&!toolsCapture);
 toolsMenu(1,nullptr);
 // Both title-bar close and Escape release this plugin's demand/capture. Game menu entry reopens tools.
 toolsClose=true;setui::draw();assert(!setui::isOpen()&&!toolsDemand&&!toolsCapture&&toolsWindows==0);
 toolsMenu(1,nullptr);assert(setui::isOpen());KilnUiKey escape{};escape.down=1;escape.vk=0x1b;
 assert(toolsKey(&escape,nullptr)&&!setui::isOpen()&&!toolsDemand&&!toolsCapture);
 setui::stop();assert(toolsUnsubscribed==3&&setui::service()->open({nullptr,0})==KILN_ERR_UNAVAILABLE);
 assert(slurp(work/"set-session.txt")=="running=0\n");
 // Last Session. The core never runs a plugin's on_unload when the game exits normally, so set-session.txt still says
 // running=1 after every session: the marker alone opens nothing. A game crash report written as the previous session
 // ended does, and that notice never takes the game's input; only SET tools the player opens do. The game's own
 // bounded crash summary is displayed, not blamed on a mod, and nothing is exported until the player chooses it.
 const auto game=work/"game",logFolder=game/"kiln/logs",controlFolder=game/"kiln/control";
 std::filesystem::create_directories(logFolder);std::filesystem::create_directories(controlFolder);std::filesystem::create_directories(game/"crash_dumps/old");
 std::ofstream(logFolder/"kiln.prev.log")<<"previous session log";
 std::ofstream(game/"crash_dumps/old/enshrouded.info")<<R"({"info":"an older crash","exception":"older exception"})";
 std::filesystem::last_write_time(game/"crash_dumps/old/enshrouded.info",std::filesystem::last_write_time(logFolder/"kiln.prev.log")-std::chrono::hours(2));
 setui::configure(logFolder,controlFolder);setui::bind(&toolsUi,1);           // a session that the game then ends normally
 assert(!setui::isOpen()&&!toolsDemand&&!toolsCapture&&slurp(controlFolder/"set-session.txt")=="running=1\n");
 setui::configure(logFolder,controlFolder);setui::bind(&toolsUi,1);           // the next start: no stop() ran in between
 if(setui::isOpen())std::cerr<<"last session: notice opened after a normal exit, capture="<<toolsCapture<<"\n";
 assert(!setui::isOpen()&&!toolsDemand&&!toolsCapture&&setui::logText(true).rfind("previous session log",0)==0);
 setui::stop();
 // A crash report from outside the previous SET session is not its crash: the marker's time is that session's start,
 // and kiln.prev.log's last write its end (plus a minute for the game to write its report).
 {const auto mark=controlFolder/"set-session.txt",prev=logFolder/"kiln.prev.log";
  auto crashAt=[&](const char* name,std::filesystem::file_time_type t){std::filesystem::create_directories(game/"crash_dumps"/name);
   const auto f=game/"crash_dumps"/name/"enshrouded.info";std::ofstream(f)<<R"({"info":"another run's crash","exception":"not this session"})";std::filesystem::last_write_time(f,t);};
  auto opensNotice=[&]{setui::configure(logFolder,controlFolder);setui::bind(&toolsUi,1);const bool open=setui::isOpen();setui::stop();return open;};
  const auto start=std::filesystem::last_write_time(prev)-std::chrono::hours(1);
  std::ofstream(mark)<<"running=1\n";std::filesystem::last_write_time(mark,start);std::filesystem::last_write_time(prev,start+std::chrono::minutes(30));
  crashAt("later",start+std::chrono::hours(3));                      // a later run without Kiln crashed
  if(opensNotice())std::cerr<<"last session: a crash 2.5 h after the SET session's log ended opened the notice\n";
  std::ofstream(mark)<<"running=1\n";std::filesystem::last_write_time(mark,start);assert(!opensNotice());
  std::filesystem::remove_all(game/"crash_dumps/later");
  std::ofstream(mark)<<"running=1\n";std::filesystem::last_write_time(mark,start);std::filesystem::last_write_time(prev,start+std::chrono::seconds(10));
  crashAt("before",start-std::chrono::seconds(30));                  // a crash just before the session started
  if(opensNotice())std::cerr<<"last session: a crash before the SET session started opened the notice\n";
  std::ofstream(mark)<<"running=1\n";std::filesystem::last_write_time(mark,start);assert(!opensNotice());
  std::filesystem::remove_all(game/"crash_dumps/before");std::ofstream(prev)<<"previous session log";
  // A start that ran no SET (play once without mods) clears the marker: a crash in it is not SET's.
  std::ofstream(mark)<<"running=1\n";crashAt("noset",std::filesystem::file_time_type::clock::now());
  setui::sessionOff(controlFolder);assert(slurp(mark)=="running=0\n"&&!opensNotice());
  std::filesystem::remove_all(game/"crash_dumps/noset");}
 std::filesystem::create_directories(game/"crash_dumps/test");std::ofstream(controlFolder/"set-session.txt")<<"running=1\n";
 std::ofstream(game/"crash_dumps/test/enshrouded.info")<<R"({"info":"fixture crash","exception":"fixture exception","source":")"
   <<std::string(199,'x')<<"\u00e9 and more\"}";  // the 200-byte cut of a value falls inside the two bytes of the e
 setui::configure(logFolder,controlFolder);setui::bind(&toolsUi,1);
 if(setui::isOpen()&&toolsCapture)std::cerr<<"last session: the crash notice took the game's input\n";
 assert(setui::isOpen()&&!setui::hasInput()&&toolsDemand==1&&toolsCapture==0);toolsRows.clear();setui::draw();
 assert(std::any_of(toolsRows.begin(),toolsRows.end(),[](const auto& row){return row.find("Last Session:")!=std::string::npos&&row.find("fixture exception")!=std::string::npos;}));
 assert(std::any_of(toolsRows.begin(),toolsRows.end(),[](const auto& row){return row.find("Escape hides")!=std::string::npos;}));
 {const auto copied=setui::logText(true);
  assert(!std::filesystem::exists(controlFolder/"set-report.txt")&&copied.find("fixture exception")<copied.find("previous session log")&&copied.find("previous session log")!=std::string::npos);
  if(!validUtf8(copied))std::cerr<<"last session: the crash summary's cut left invalid UTF-8\n";
  assert(validUtf8(copied)&&copied.find("source="+std::string(199,'x')+"\n")!=std::string::npos);}
 assert(toolsKey(&escape,nullptr)&&!setui::isOpen()&&!toolsDemand&&!toolsCapture);  // Escape hides the notice
 toolsMenu(1,nullptr);assert(setui::isOpen()&&setui::hasInput()&&toolsDemand==1&&toolsCapture==1);  // SET tools from the game menu
 toolsRows.clear();setui::draw();
 assert(std::any_of(toolsRows.begin(),toolsRows.end(),[](const auto& row){return row.find("Last Session:")!=std::string::npos;}));
 toolsClick="Dismiss";setui::draw();toolsRows.clear();setui::draw();
 assert(setui::isOpen()&&toolsCapture==1&&std::none_of(toolsRows.begin(),toolsRows.end(),[](const auto& row){return row.find("Last Session:")!=std::string::npos;}));
 assert(toolsKey(&escape,nullptr)&&!setui::isOpen()&&!setui::hasInput()&&!toolsDemand&&!toolsCapture);setui::stop();
 setui::configure(logFolder,controlFolder);setui::bind(&toolsUi,1);assert(!setui::isOpen());setui::stop();
 std::cout<<"SET tools settings/onChange, precision, compatibility, reporting/crash prompt, console and lifecycle: PASS\n";
}
