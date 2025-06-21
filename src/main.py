
import sys
from PySide6.QtWidgets import QApplication
from ui.main_window import MainWindow

def main():
    try:
        app = QApplication(sys.argv)
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
