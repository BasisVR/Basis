use std::collections::VecDeque;
use std::sync::Mutex;

pub struct Queue<T> {
    items: Mutex<VecDeque<T>>,
    cap: usize,
}

impl<T> Queue<T> {
    pub fn new(cap: usize) -> Self {
        Self {
            items: Mutex::new(VecDeque::with_capacity(cap.min(64))),
            cap,
        }
    }

    pub fn push(&self, item: T) -> bool {
        let mut items = self.items.lock().unwrap_or_else(|e| e.into_inner());
        if items.len() >= self.cap {
            return false;
        }
        items.push_back(item);
        true
    }

    pub fn pop(&self) -> Option<T> {
        self.items
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .pop_front()
    }

    pub fn clear(&self) {
        self.items.lock().unwrap_or_else(|e| e.into_inner()).clear();
    }

    pub fn len(&self) -> usize {
        self.items.lock().unwrap_or_else(|e| e.into_inner()).len()
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}
