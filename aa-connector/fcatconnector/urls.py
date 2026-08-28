from django.urls import path

from . import views

app_name = "fcatconnector"

# Order matters. Alliance Auth's decorate_url_patterns stops walking the list at the first
# excluded view, so anything listed after the API would silently lose its main-character gate.
# Browser pages first, API last.
urlpatterns = [
    # The FC's own page
    path("", views.keys, name="keys"),
    path("keys/new/", views.create_key, name="create_key"),
    path("keys/<int:key_id>/revoke/", views.revoke_key, name="revoke_key"),
    # What FCAT talks to. Excluded from main_character_required in auth_hooks.py; these
    # authenticate with the key header instead.
    path("api/", views.index, name="api_index"),
    path("api/doctrines/", views.doctrines, name="api_doctrines"),
    path("api/structures/", views.structures, name="api_structures"),
]
