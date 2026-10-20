from PIL import Image

def create_wix_images(logo_path):
    try:
        logo = Image.open(logo_path).convert("RGBA")
        
        # WixUIDialogBmp: 493x312
        # Left 164x312 is the image area, rest is white
        dialog = Image.new("RGB", (493, 312), (255, 255, 255))
        left_bg = Image.new("RGB", (164, 312), (21, 23, 30))
        dialog.paste(left_bg, (0, 0))
        
        logo_dialog = logo.copy()
        logo_dialog.thumbnail((140, 140))
        x_offset = (164 - logo_dialog.width) // 2
        y_offset = (312 - logo_dialog.height) // 2
        dialog.paste(logo_dialog, (x_offset, y_offset), logo_dialog)
        dialog.save("SetupProject/dialog.bmp")

        # WixUIBannerBmp: 493x58
        banner = Image.new("RGB", (493, 58), (255, 255, 255))
        logo_banner = logo.copy()
        logo_banner.thumbnail((48, 48))
        banner.paste(logo_banner, (493 - 48 - 5, (58 - logo_banner.height) // 2), logo_banner)
        banner.save("SetupProject/banner.bmp")
        
        print("Images created successfully")
    except Exception as e:
        print("Error:", e)

create_wix_images("Alphan Logo.PNG")
