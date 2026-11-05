// fixture: the mod registers its own data-pack section
class M { void OnLoad() { Content.RegisterSection("mysection", c => { }); } }
