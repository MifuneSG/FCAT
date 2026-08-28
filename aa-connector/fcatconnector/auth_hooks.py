from allianceauth import hooks
from allianceauth.services.hooks import MenuItemHook, UrlHook

from . import __title__, urls


class FcatConnectorMenu(MenuItemHook):
    def __init__(self):
        super().__init__(__title__, "fa-solid fa-plug fa-fw", "fcatconnector:keys", navactive=["fcatconnector:"])

    def render(self, request):
        # Invisible to anyone without the permission, so members never see it.
        if request.user.has_perm("fcatconnector.basic_access"):
            return MenuItemHook.render(self, request)
        return ""


@hooks.register("menu_item_hook")
def register_menu():
    return FcatConnectorMenu()


@hooks.register("url_hook")
def register_urls():
    # Alliance Auth wraps every hooked URL in main_character_required, which is
    # login_required underneath. FCAT calls the API with a key and no session, so left
    # alone the endpoints answer a 302 to the login page instead of JSON. The three API
    # views opt out and do their own auth in the @api decorator; the FC's page keeps the
    # default gate, because a browser session is exactly what it wants.
    return UrlHook(
        urls,
        "fcatconnector",
        r"^fcat/",
        excluded_views=[
            "fcatconnector.views.index",
            "fcatconnector.views.doctrines",
            "fcatconnector.views.structures",
        ],
    )
