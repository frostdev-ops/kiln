// Windows: Kiln's icon and description in kilnctl.exe's resources (tools/winres/winres.rs). Other targets: nothing.
#[path = "../../tools/winres/winres.rs"]
mod winres;

fn main() {
    winres::embed("kilnctl", "Kiln server runner (kilnctl)");
}
