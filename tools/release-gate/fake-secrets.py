# fake-secrets.py LOG — a minimal org.freedesktop.secrets on the session bus it is started on,
# holding one secret given by the environment (GATE_ATTRS "k=v,k=v", GATE_SECRET). Plain sessions
# only. For the send/receive gate inside a private nested session; it never touches a real keyring.
import os, sys, time
import dbus, dbus.service, dbus.exceptions
from dbus.mainloop.glib import DBusGMainLoop
from gi.repository import GLib

LOG = sys.argv[1]
ATTRS = dict(kv.split("=", 1) for kv in os.environ["GATE_ATTRS"].split(","))
SECRET = os.environ["GATE_SECRET"].encode()
BASE = "/org/freedesktop/secrets"
COLL = BASE + "/collection/login"
ITEM = COLL + "/1"
SESSION = BASE + "/session/1"
PROPS = "org.freedesktop.DBus.Properties"

def log(s):
    with open(LOG, "a") as f: f.write(time.strftime("%H:%M:%S ") + s + "\n")

class Obj(dbus.service.Object):
    props = {}
    @dbus.service.method(PROPS, in_signature="ss", out_signature="v")
    def Get(self, iface, name): return self.props()[name]
    @dbus.service.method(PROPS, in_signature="s", out_signature="a{sv}")
    def GetAll(self, iface): return self.props()
    @dbus.service.method(PROPS, in_signature="ssv")
    def Set(self, iface, name, value): pass

class Service(Obj):
    def props(self): return {"Collections": dbus.Array([dbus.ObjectPath(COLL)], signature="o")}
    @dbus.service.method("org.freedesktop.Secret.Service", in_signature="sv", out_signature="vo")
    def OpenSession(self, algorithm, inp):
        log(f"OpenSession {algorithm}")
        if algorithm != "plain":
            raise dbus.exceptions.DBusException("only plain", name="org.freedesktop.DBus.Error.NotSupported")
        return dbus.String("", variant_level=1), dbus.ObjectPath(SESSION)
    @dbus.service.method("org.freedesktop.Secret.Service", in_signature="a{ss}", out_signature="aoao")
    def SearchItems(self, attrs):
        hit = all(ATTRS.get(k) == v for k, v in attrs.items())
        log(f"SearchItems {dict(attrs)} -> {'hit' if hit else 'none'}")
        return (dbus.Array([dbus.ObjectPath(ITEM)] if hit else [], signature="o"), dbus.Array([], signature="o"))
    @dbus.service.method("org.freedesktop.Secret.Service", in_signature="ao", out_signature="aoo")
    def Unlock(self, objects): return (dbus.Array(objects, signature="o"), dbus.ObjectPath("/"))
    @dbus.service.method("org.freedesktop.Secret.Service", in_signature="aoo", out_signature="a{o(oayays)}")
    def GetSecrets(self, items, session):
        log(f"GetSecrets {len(items)} item(s)")
        return dbus.Dictionary({dbus.ObjectPath(ITEM): (dbus.ObjectPath(SESSION), dbus.ByteArray(b""), dbus.ByteArray(SECRET), "text/plain")}, signature="o(oayays)")
    @dbus.service.method("org.freedesktop.Secret.Service", in_signature="s", out_signature="o")
    def ReadAlias(self, name): return dbus.ObjectPath(COLL)

class Collection(Obj):
    def props(self):
        return {"Items": dbus.Array([dbus.ObjectPath(ITEM)], signature="o"), "Label": "Login",
                "Locked": False, "Created": dbus.UInt64(0), "Modified": dbus.UInt64(0)}
    @dbus.service.method("org.freedesktop.Secret.Collection", in_signature="a{ss}", out_signature="ao")
    def SearchItems(self, attrs):
        return dbus.Array([dbus.ObjectPath(ITEM)] if all(ATTRS.get(k) == v for k, v in attrs.items()) else [], signature="o")

class Item(Obj):
    def props(self):
        return {"Locked": False, "Attributes": dbus.Dictionary(ATTRS, signature="ss"), "Label": "gate",
                "Created": dbus.UInt64(0), "Modified": dbus.UInt64(0)}
    @dbus.service.method("org.freedesktop.Secret.Item", in_signature="o", out_signature="(oayays)")
    def GetSecret(self, session):
        log("Item.GetSecret")
        return (dbus.ObjectPath(SESSION), dbus.ByteArray(b""), dbus.ByteArray(SECRET), "text/plain")

class Session(Obj):
    def props(self): return {}
    @dbus.service.method("org.freedesktop.Secret.Session")
    def Close(self): pass

DBusGMainLoop(set_as_default=True)
bus = dbus.SessionBus()
name = dbus.service.BusName("org.freedesktop.secrets", bus)
Service(bus, BASE); Collection(bus, COLL); Item(bus, ITEM); Session(bus, SESSION)
log("fake secret service ready")
GLib.MainLoop().run()
