import sys
from pathlib import Path

from PySide6.QtWidgets import QApplication
from PySide6.QtGui     import QIcon
from ui.main_window    import MainWindow

def resource_path(relative_path: str) -> Path:
    """
    Get the absolute path to a resource, whether running
    from source or bundled by PyInstaller (--onefile).
    """
    # If running as a PyInstaller bundle, _MEIPASS is the unpack directory
    if getattr(sys, "_MEIPASS", None):
        base_path = Path(sys._MEIPASS)
    else:
        # Otherwise use the directory where this script lives
        base_path = Path(__file__).resolve().parent
    return base_path / relative_path

def main():
    try:
        app = QApplication(sys.argv)

        # Load our application icon
        icon_file = resource_path("app_icon.ico")
        if icon_file.exists():
            app.setWindowIcon(QIcon(str(icon_file)))
        else:
            print(f"[Warning] Icon file not found at {icon_file}")

        window = MainWindow()
        window.show()
        sys.exit(app.exec())
    except Exception as e:
        print("FCAT crashed due to an exception:")
        print(e)
        input("Press Enter to close...")

if __name__ == "__main__":
    print("Starting FCAT...")
    main()
