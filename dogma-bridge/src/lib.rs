//! A C ABI around EVEShipFit's dogma engine.
//!
//! FCAT needs real fit statistics - damage, effective hitpoints, and above all MASS, which decides
//! whether a fleet fits through a wormhole. Those numbers come out of EVE's dogma: every modifier
//! applied in the right order, with stacking penalties, and each ship bonus gated on the skill it
//! belongs to. That is a solved problem and this does not re-solve it. It calls
//! [EVEShipFit's engine](https://github.com/EVEShipFit/dogma-engine) (MIT), which is the same one
//! eveship.fit and zKillboard's fitting view use.
//!
//! The engine is a plain Rust library, so it compiles to an ordinary Windows DLL. Nothing here
//! opens a socket, spawns a process, or embeds a browser: `sde.dat` is a local file, `calculate`
//! is a pure function, and the whole thing runs inside FCAT's own process with no network at all.
//! That matters - FCAT is an FC's tool and the EVE community is right to be wary of third-party
//! software that phones home. This one cannot.
//!
//! The shape mirrors `esf-wasm`, the engine's own WASM wrapper, with JSON over the boundary
//! instead of JsValue.

use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::sync::OnceLock;

use esf_data::{InfoSde, Sde};
use esf_dogma_engine::{Fit, Options};

/// The SDE is handed over once and read in place for the rest of the process. Holding the bytes in
/// a `OnceLock` is what makes them `'static`, which is what lets `Sde` borrow them without unsafe.
static SDE_BYTES: OnceLock<Vec<u8>> = OnceLock::new();
static SDE: OnceLock<Sde<'static>> = OnceLock::new();

/// Load `sde.dat`. Returns the EVE build number it was made from, or a negative error code.
///
/// Call once before [`fcat_dogma_calculate`]. Calling it twice is an error: the first buffer is
/// borrowed for the rest of the session.
///
/// # Safety
/// `ptr` must point at `len` readable bytes for the duration of the call.
#[no_mangle]
pub unsafe extern "C" fn fcat_dogma_load_sde(ptr: *const u8, len: usize) -> i32 {
    if ptr.is_null() || len == 0 {
        return ERR_BAD_ARGUMENT;
    }

    catch_unwind(AssertUnwindSafe(|| {
        if SDE.get().is_some() {
            return ERR_ALREADY_LOADED;
        }

        let bytes = SDE_BYTES.get_or_init(|| std::slice::from_raw_parts(ptr, len).to_vec());

        let Ok(sde) = Sde::new(bytes) else {
            return ERR_BAD_SDE;
        };

        let build_number = sde.build_number();
        let _ = SDE.set(sde);
        build_number
    }))
    .unwrap_or(ERR_PANIC)
}

/// Calculate a fit. Takes the fit as JSON, returns the calculation as JSON.
///
/// The returned pointer is owned by the caller and must be handed back to [`fcat_dogma_free`].
/// Returns null only if the argument was null or not UTF-8; every other failure comes back as a
/// JSON object with an `error` key, so the caller has something to log rather than a bare null.
///
/// # Safety
/// `fit_json` must be a valid NUL-terminated C string.
#[no_mangle]
pub unsafe extern "C" fn fcat_dogma_calculate(fit_json: *const c_char) -> *mut c_char {
    if fit_json.is_null() {
        return ptr::null_mut();
    }

    let Ok(input) = CStr::from_ptr(fit_json).to_str() else {
        return ptr::null_mut();
    };

    // A panic crossing a C ABI boundary is undefined behaviour, and this DLL sits inside FCAT's
    // process - an unlucky fit must not be able to take an FC's tool down mid-fight.
    let result = catch_unwind(AssertUnwindSafe(|| run(input)))
        .unwrap_or_else(|_| error_json("the dogma engine panicked on this fit"));

    match CString::new(result) {
        Ok(s) => s.into_raw(),
        Err(_) => ptr::null_mut(),
    }
}

/// Release a string returned by [`fcat_dogma_calculate`].
///
/// # Safety
/// `s` must be a pointer returned by this library, and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn fcat_dogma_free(s: *mut c_char) {
    if !s.is_null() {
        drop(CString::from_raw(s));
    }
}

/// The engine version this DLL was built against, so FCAT can log what it is talking to.
#[no_mangle]
pub extern "C" fn fcat_dogma_version() -> *const c_char {
    // A static NUL-terminated string; never freed, never owned by the caller.
    concat!(env!("CARGO_PKG_VERSION"), "\0").as_ptr() as *const c_char
}

const ERR_BAD_ARGUMENT: i32 = -1;
const ERR_ALREADY_LOADED: i32 = -2;
const ERR_BAD_SDE: i32 = -3;
const ERR_PANIC: i32 = -4;

/// The `{"fit": …, "options": …}` envelope FCAT sends.
fn run(input: &str) -> String {
    let Some(sde) = SDE.get() else {
        return error_json("sde.dat has not been loaded");
    };

    let envelope: serde_json::Value = match serde_json::from_str(input) {
        Ok(v) => v,
        Err(e) => return error_json(&format!("could not read the fit: {e}")),
    };

    // Accept either a bare fit or the envelope, so the caller can stay simple.
    let (fit_value, options_value) = match envelope.get("fit") {
        Some(fit) => (fit.clone(), envelope.get("options").cloned()),
        None => (envelope, None),
    };

    let fit: Fit = match serde_json::from_value(fit_value) {
        Ok(f) => f,
        Err(e) => return error_json(&format!("that is not a fit the engine understands: {e}")),
    };

    let options: Options = match options_value {
        Some(v) if !v.is_null() => match serde_json::from_value(v) {
            Ok(o) => o,
            Err(e) => return error_json(&format!("bad options: {e}")),
        },
        _ => Options::default(),
    };

    let calculation = esf_dogma_engine::calculate(&InfoSde::new(sde), &fit, &options);

    match serde_json::to_string(&calculation) {
        Ok(json) => json,
        Err(e) => error_json(&format!("could not write the result: {e}")),
    }
}

fn error_json(message: &str) -> String {
    serde_json::json!({ "error": message }).to_string()
}
