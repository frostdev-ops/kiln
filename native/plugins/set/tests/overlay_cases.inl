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
 // An unclean previous session prompts without exporting anything; dismissal releases capture. Normal teardown
 // marks the next boot clean. The game's own bounded crash summary is displayed, not blamed on a mod.
 const auto game=work/"game",logFolder=game/"kiln/logs",controlFolder=game/"kiln/control";
 std::filesystem::create_directories(logFolder);std::filesystem::create_directories(controlFolder);std::filesystem::create_directories(game/"crash_dumps/test");
 std::ofstream(controlFolder/"set-session.txt")<<"running=1\n";std::ofstream(logFolder/"kiln.prev.log")<<"previous crash log";
 std::ofstream(game/"crash_dumps/test/enshrouded.info")<<R"({"info":"fixture crash","exception":"fixture exception"})";
 setui::configure(logFolder,controlFolder);setui::bind(&toolsUi,1);assert(setui::isOpen());toolsRows.clear();setui::draw();
 assert(std::any_of(toolsRows.begin(),toolsRows.end(),[](const auto& row){return row.find("Last Session:")!=std::string::npos&&row.find("fixture exception")!=std::string::npos;}));
 assert(!std::filesystem::exists(controlFolder/"set-report.txt")&&setui::logText(true).find("previous crash log")!=std::string::npos);
 toolsClick="Dismiss";setui::draw();assert(!setui::isOpen()&&!toolsDemand&&!toolsCapture);setui::stop();
 setui::configure(logFolder,controlFolder);setui::bind(&toolsUi,1);assert(!setui::isOpen());setui::stop();
 std::cout<<"SET tools settings/onChange, precision, compatibility, reporting/crash prompt, console and lifecycle: PASS\n";
}
