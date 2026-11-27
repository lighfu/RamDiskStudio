fn main() {
    println!("cargo:rerun-if-changed=assets/ramdisk.ico");
    winresource::WindowsResource::new()
        .set_icon("assets/ramdisk.ico")
        .set("ProductName", "RAM Disk Studio")
        .compile()
        .expect("embed Windows resources");
}
