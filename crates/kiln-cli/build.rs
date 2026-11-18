// Windows: Kiln's icon and description in kiln.exe's resources (tools/winres/winres.rs). Other targets: nothing.
#[path = "../../tools/winres/winres.rs"]
mod winres;

fn main() {
    winres::embed("kiln", "Kiln command line for mod makers (kiln)");
}
