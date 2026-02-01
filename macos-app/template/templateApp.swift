//
//  templateApp.swift
//  template
//
//  Created by 程龙 on 2026/2/1.
//

import SwiftUI

@main
struct templateApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate

    var body: some Scene {
        WindowGroup {
            ContentView()
        }
    }
}
